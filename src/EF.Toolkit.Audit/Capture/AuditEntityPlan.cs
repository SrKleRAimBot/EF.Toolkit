using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Capture;

/// <summary>
///     What is captured for one entity type, resolved once and reused.
/// </summary>
/// <remarks>
///     <para>
///         Resolving registration means reading several annotations, reconciling fluent
///         configuration with attributes, and deciding property by property what is excluded, masked
///         or redacted. None of that can change once the model is finalized, and all of it would
///         otherwise be redone for every row of every save.
///     </para>
///     <para>
///         Cached as a runtime annotation on the entity type rather than in a static dictionary, for
///         the reason EF.Toolkit.Bulk's <c>BulkAnnotations</c> sets out at length: a static cache
///         keyed by metadata is a GC root for every model the process has ever built, and a host
///         with a model per tenant then never lets one go.
///     </para>
/// </remarks>
internal sealed class AuditEntityPlan
{
    private readonly Dictionary<IProperty, AuditPropertyPlan> _captured;

    private AuditEntityPlan(
        bool isAudited,
        string? notAuditedReason,
        AuditOperations operations,
        string entityTypeName,
        Dictionary<IProperty, AuditPropertyPlan> captured,
        IReadOnlyList<IProperty> keyProperties,
        IReadOnlyList<IProperty> primaryKey,
        IProperty? tenantProperty,
        IReadOnlyList<IProperty> ownProperties,
        IReadOnlyList<AuditOwnedFold> ownedFolds)
    {
        IsAudited = isAudited;
        NotAuditedReason = notAuditedReason;
        Operations = operations;
        EntityTypeName = entityTypeName;
        _captured = captured;
        KeyProperties = keyProperties;
        PrimaryKey = primaryKey;
        TenantProperty = tenantProperty;
        OwnProperties = ownProperties;
        OwnedFolds = ownedFolds;
    }

    /// <summary>Whether this entity type produces audit entries at all.</summary>
    public bool IsAudited { get; }

    /// <summary>Why it does not, when <see cref="IsAudited" /> is <see langword="false" />.</summary>
    public string? NotAuditedReason { get; }

    /// <summary>Which operations produce an entry.</summary>
    public AuditOperations Operations { get; }

    /// <summary>What the entry's entity-type column holds.</summary>
    public string EntityTypeName { get; }

    /// <summary>The properties the entry's key is built from.</summary>
    public IReadOnlyList<IProperty> KeyProperties { get; }

    /// <summary>The primary key, written into the payload's <c>key</c> object.</summary>
    public IReadOnlyList<IProperty> PrimaryKey { get; }

    /// <summary>The property the tenant is read from, or <see langword="null" />.</summary>
    public IProperty? TenantProperty { get; }

    /// <summary>The recorded properties declared on this entity type itself.</summary>
    public IReadOnlyList<IProperty> OwnProperties { get; }

    /// <summary>
    ///     Owned references that share this type's table, and are therefore recorded as part of its
    ///     entry rather than as entries of their own.
    /// </summary>
    public IReadOnlyList<AuditOwnedFold> OwnedFolds { get; }

    /// <summary>Gets, or resolves and caches, the plan for <paramref name="entityType" />.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="options">The context's auditing settings.</param>
    /// <remarks>
    ///     The cache is keyed by entity type alone, not by entity type and settings, which is safe
    ///     for a reason worth stating: every setting contributes to
    ///     <c>AuditOptionsExtension.GetServiceProviderHashCode</c>, so two differently-configured
    ///     contexts get different internal service providers and therefore different model
    ///     instances. One model can only ever have been built under one set of settings.
    /// </remarks>
    public static AuditEntityPlan For(IEntityType entityType, AuditOptions options)
        => entityType.GetOrAddRuntimeAnnotationValue(
            AuditAnnotations.Plan,
            static key => Build(key.EntityType, key.Options),
            (EntityType: entityType, Options: options));

    /// <summary>Whether an operation on this type produces an entry.</summary>
    /// <param name="operation">The operation.</param>
    public bool Audits(AuditOperation operation)
        => IsAudited
            && Operations.HasFlag(operation switch
            {
                AuditOperation.Insert => AuditOperations.Insert,
                AuditOperation.Update => AuditOperations.Update,
                _ => AuditOperations.Delete,
            });

    /// <summary>How <paramref name="property" /> is captured, or <see langword="null" /> if it is not.</summary>
    /// <param name="property">The property.</param>
    public AuditPropertyPlan? Capture(IProperty property)
        => _captured.GetValueOrDefault(property);

    private static AuditEntityPlan Build(IEntityType entityType, AuditOptions options)
    {
        var (isAudited, reason) = Registration(entityType, options);

        var operations = entityType.FindAnnotation(AuditAnnotations.Operations)?.Value
            is AuditOperations configured
            ? configured
            : options.Operations;

        var primaryKey = entityType.FindPrimaryKey()?.Properties ?? [];
        var captured = new Dictionary<IProperty, AuditPropertyPlan>();
        var own = new List<IProperty>();
        var folds = new List<AuditOwnedFold>();
        var keyProperties = primaryKey;
        IProperty? tenantProperty = null;

        if (isAudited)
        {
            AddProperties(entityType, entityType, options, prefix: null, captured, own);
            AddOwnedFolds(entityType, entityType, options, prefix: null, path: [], captured, folds);

            keyProperties = ResolveKeyProperties(entityType, primaryKey);
            tenantProperty = options.TenantPropertyName is { } name
                ? entityType.FindProperty(name)
                : null;
        }

        return new AuditEntityPlan(
            isAudited,
            reason,
            operations,
            ResolveEntityTypeName(entityType, options),
            captured,
            keyProperties,
            primaryKey,
            tenantProperty,
            own,
            folds);
    }

    /// <summary>Adds one entity type's own properties to the capture set.</summary>
    /// <remarks>
    ///     <paramref name="declaring" /> may be an owned type being folded into
    ///     <paramref name="root" />, in which case exclusions and masks are read from the owned type
    ///     — it is where <c>[AuditMask]</c> would sit on a value object — and the payload name is
    ///     prefixed with the navigation path that reaches it.
    /// </remarks>
    private static void AddProperties(
        IEntityType root,
        ITypeBase declaring,
        AuditOptions options,
        string? prefix,
        Dictionary<IProperty, AuditPropertyPlan> captured,
        List<IProperty> collected)
    {
        _ = root;

        var excluded = Excluded(declaring);
        var masked = Masked(declaring);

        foreach (var property in declaring.GetProperties())
        {
            if (excluded.Contains(property.Name))
            {
                continue;
            }

            if (prefix is not null && property.IsPrimaryKey())
            {
                // An owned type sharing its owner's table has the owner's key as its own. Recording
                // it again under the navigation's name would be the same value twice.
                continue;
            }

            var isMasked = masked.TryGetValue(property.Name, out var redactor);

            if (!isMasked
                && (property.FindAnnotation(AuditAnnotations.MaskedByAttribute)?.Value is true
                    || options.MaskPredicate?.Invoke(property) == true))
            {
                isMasked = true;
            }

            var name = options.PayloadNames == AuditPayloadNames.Column
                ? ColumnName(property)
                : property.Name;

            captured[property] = new AuditPropertyPlan(
                property,
                prefix is null ? name : $"{prefix}.{name}",
                isMasked,
                redactor);

            collected.Add(property);
        }

        AddComplexProperties(root, declaring, options, prefix, captured, collected);
    }

    /// <summary>
    ///     Folds a complex property's own properties into the capture set that contains it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A complex type is not an entity type and has no identity of its own: its values are
    ///         columns of the row that declares it, so a change to one is a change to that row and
    ///         belongs in that row's entry. Because those columns are mapped by the complex type
    ///         rather than by the entity, walking the entity's own properties missed them entirely
    ///         and produced a trail that quietly disagreed with the table it described.
    ///     </para>
    ///     <para>
    ///         Named by the path that reaches them — <c>Money.Amount</c> — for the same reason an
    ///         owned reference is: the payload has to say which value moved, and <c>Amount</c> alone
    ///         would collide with any other complex value carrying the same member name. Nesting is
    ///         followed to any depth, so <c>Money.Audit.By</c> reads as it is written.
    ///     </para>
    ///     <para>
    ///         The exclusion and masking annotations are read from the complex type itself, so
    ///         <c>[AuditMask]</c> sits on the value object where the sensitive member is declared
    ///         rather than being restated by every entity that uses it.
    ///     </para>
    /// </remarks>
    private static void AddComplexProperties(
        IEntityType root,
        ITypeBase declaring,
        AuditOptions options,
        string? prefix,
        Dictionary<IProperty, AuditPropertyPlan> captured,
        List<IProperty> collected)
    {
        foreach (var complex in declaring.GetComplexProperties())
        {
            if (complex.IsCollection)
            {
                // A complex collection is stored as one JSON column, which the owner already
                // captures as a property in its own right.
                continue;
            }

            var nestedPrefix = prefix is null ? complex.Name : $"{prefix}.{complex.Name}";

            AddProperties(root, complex.ComplexType, options, nestedPrefix, captured, collected);
        }
    }

    /// <summary>
    ///     Folds owned references that share this type's table into its capture set.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Table splitting means the owned value lives in the owner's row, so a change to it is
    ///         a change to that row. EF still tracks it as a separate entry, and treating that as a
    ///         separate audit entry would produce two entries for one row — one of them named after
    ///         a type the application does not think of as a thing at all.
    ///     </para>
    ///     <para>
    ///         Owned <em>collections</em> are not folded: they have tables and keys of their own, so
    ///         they are ordinary entity types as far as auditing is concerned and are registered
    ///         like any other.
    ///     </para>
    /// </remarks>
    private static void AddOwnedFolds(
        IEntityType root,
        IEntityType declaring,
        AuditOptions options,
        string? prefix,
        IReadOnlyList<INavigation> path,
        Dictionary<IProperty, AuditPropertyPlan> captured,
        List<AuditOwnedFold> folds)
    {
        foreach (var navigation in declaring.GetNavigations())
        {
            var target = navigation.TargetEntityType;

            if (!target.IsOwned()
                || navigation.IsCollection
                || target.GetTableName() != root.GetTableName()
                || target.GetSchema() != root.GetSchema())
            {
                continue;
            }

            var nested = new List<INavigation>(path) { navigation };
            var nestedPrefix = prefix is null ? navigation.Name : $"{prefix}.{navigation.Name}";
            var properties = new List<IProperty>();

            AddProperties(root, target, options, nestedPrefix, captured, properties);
            folds.Add(new AuditOwnedFold(target, nested, properties));

            AddOwnedFolds(root, target, options, nestedPrefix, nested, captured, folds);
        }
    }

    /// <summary>
    ///     Works out whether this type is audited, and says why when it is not.
    /// </summary>
    /// <remarks>
    ///     Fluent configuration wins over an attribute. Where the two disagree outright the model
    ///     validator has already refused to build the model, so by the time this runs the only
    ///     disagreement left is the harmless one — an attribute saying nothing.
    /// </remarks>
    private static (bool IsAudited, string? Reason) Registration(
        IEntityType entityType,
        AuditOptions options)
    {
        if (typeof(AuditEntry).IsAssignableFrom(entityType.ClrType))
        {
            // Auditing the audit table would be an unbounded loop, and there is nothing useful in
            // an entry recording that an entry was written.
            return (false, "it is the audit entry type itself");
        }

        var stated = entityType.FindAnnotation(AuditAnnotations.Audited)?.Value as bool?
            ?? entityType.FindAnnotation(AuditAnnotations.AuditedByAttribute)?.Value as bool?;

        if (stated == false)
        {
            return (false, "it is registered as not audited");
        }

        if (entityType.IsOwned()
            && entityType.FindOwnership() is { IsUnique: true } ownership
            && entityType.GetTableName() == ownership.PrincipalEntityType.GetTableName()
            && entityType.GetSchema() == ownership.PrincipalEntityType.GetSchema())
        {
            // Its values live in the owner's row, and are recorded as part of the owner's entry.
            return (false, "it is folded into its owner's audit entry");
        }

        if (entityType.FindPrimaryKey() is null)
        {
            // Under AuditAllEntities this sweeps up query types and keyless views, which is why it
            // is a skip and not a refusal. IsAudited() on a keyless type is refused separately, by
            // the model validator, where the user asked for something impossible rather than
            // merely not excluding it.
            return (false, "it has no primary key");
        }

        if (entityType.GetTableName() is null)
        {
            return (false, "it is not mapped to a table");
        }

        return stated == true || options.AuditAllEntities
            ? (true, null)
            : (false, "it is not registered for auditing");
    }

    private static HashSet<string> Excluded(ITypeBase entityType)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);

        if (entityType.FindAnnotation(AuditAnnotations.ExcludedProperties)?.Value
            is IReadOnlyList<string> fluent)
        {
            excluded.UnionWith(fluent);
        }

        foreach (var property in entityType.GetProperties())
        {
            if (property.FindAnnotation(AuditAnnotations.IgnoredByAttribute)?.Value is true)
            {
                excluded.Add(property.Name);
            }
        }

        return excluded;
    }

    private static Dictionary<string, Func<object?, object?>?> Masked(ITypeBase entityType)
        => entityType.FindAnnotation(AuditAnnotations.MaskedProperties)?.Value
            is IReadOnlyDictionary<string, Func<object?, object?>?> fluent
            ? new Dictionary<string, Func<object?, object?>?>(fluent, StringComparer.Ordinal)
            : new Dictionary<string, Func<object?, object?>?>(StringComparer.Ordinal);

    private static IReadOnlyList<IProperty> ResolveKeyProperties(
        IEntityType entityType,
        IReadOnlyList<IProperty> primaryKey)
    {
        if (entityType.FindAnnotation(AuditAnnotations.KeyProperties)?.Value
            is not IReadOnlyList<string> names)
        {
            return primaryKey;
        }

        var properties = new List<IProperty>(names.Count);
        foreach (var name in names)
        {
            properties.Add(
                entityType.FindProperty(name)
                ?? throw new AuditNotSupportedException(
                    $"KeyFrom named '{entityType.DisplayName()}.{name}', which is not a mapped "
                    + "property."));
        }

        return properties;
    }

    private static string ResolveEntityTypeName(IEntityType entityType, AuditOptions options)
        => options.StoreEntityTypeAs switch
        {
            AuditEntityTypeNames.FullName => entityType.ClrType.FullName ?? entityType.Name,
            AuditEntityTypeNames.TableName => entityType.GetTableName() ?? entityType.ShortName(),
            _ => entityType.ShortName(),
        };

    private static string ColumnName(IProperty property)
    {
        var table = StoreObjectIdentifier.Create(property.DeclaringType, StoreObjectType.Table);

        return table is null
            ? property.Name
            : property.GetColumnName(table.Value) ?? property.Name;
    }
}

/// <summary>How one property is recorded.</summary>
/// <param name="Property">The property.</param>
/// <param name="Name">What it is called in the payload.</param>
/// <param name="IsMasked">Whether its value is replaced rather than recorded.</param>
/// <param name="Redactor">
///     Turns the value into what is recorded, or <see langword="null" /> for the configured mask
///     token. Only consulted when <paramref name="IsMasked" /> is <see langword="true" />.
/// </param>
internal sealed record AuditPropertyPlan(
    IProperty Property,
    string Name,
    bool IsMasked,
    Func<object?, object?>? Redactor);

/// <summary>An owned reference recorded as part of its owner's entry.</summary>
/// <param name="EntityType">The owned type.</param>
/// <param name="Path">The navigations leading from the owner to it.</param>
/// <param name="Properties">Its recorded properties.</param>
internal sealed record AuditOwnedFold(
    IEntityType EntityType,
    IReadOnlyList<INavigation> Path,
    IReadOnlyList<IProperty> Properties);
