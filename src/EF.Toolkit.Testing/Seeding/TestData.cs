using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Testing;

/// <summary>Builds valid entities from the model, so a test only states the values it is about.</summary>
/// <remarks>
///     <para>
///         Every required scalar is given a value that is valid for its column — strings within their
///         maximum length, decimals within their precision, dates within every engine's range — and
///         unique: one counter runs across every value the builder produces, so two entities never
///         share a generated string or number and a unique index is never tripped by accident.
///     </para>
///     <para>
///         Store-generated properties are left alone for the database to fill, and so are foreign-key
///         properties: a required reference is satisfied by building its principal too, recursively,
///         up to <see cref="MaxDepth" />. Nothing is reused between calls — each entity gets principals
///         of its own — so set the navigation, or the foreign key, in the customize callback to share
///         one.
///     </para>
///     <para>
///         Deterministic: the same seed and the same sequence of calls build the same values. An
///         instance is safe to share between threads, but then the sequence of calls is not fixed.
///     </para>
/// </remarks>
public sealed class TestData
{
    private static readonly DateTime BaseDate = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly ConditionalWeakTable<IDbContextOptions, TestData> ByOptions = new();

    // By the context itself too: a context whose OnConfiguring adds an option ends up with options of its
    // own, which the database's registration never saw.
    private static readonly ConditionalWeakTable<DbContext, TestData> ByContext = new();

    private readonly Lock _lock = new();
    private readonly Random _random;
    private long _counter;

    // A column with only so many values gets them in turn from a count of its own: the shared counter
    // moves once per generated value, so with three on a row a decimal(2,2) came round to a repeat after
    // 34 rows of its 100. Keyed by the table and column the value lands in — TPH siblings sharing a
    // column share its count, TPT and TPC tables each keep their own — or, for a primary key, which has
    // to be unique across a TPC hierarchy's tables, and a column outside any table, by the hierarchy.
    private readonly Dictionary<(object Scope, string Column), long> _boundedCounters = [];

    // The entity whose own properties are being filled, for the table a base property lands in.
    private IEntityType? _filling;

    /// <summary>Creates a builder.</summary>
    /// <param name="seed">Seeds the values. The same seed and the same calls build the same values.</param>
    /// <param name="maxDepth">How many levels of required principals to build. Default 3.</param>
    public TestData(int seed = 0, int maxDepth = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);

        Seed = seed;
        MaxDepth = maxDepth;
        _random = new Random(seed);
    }

    /// <summary>The builder <c>context.Build&lt;T&gt;()</c> uses for contexts not created by a test database.</summary>
    public static TestData Shared { get; } = new();

    /// <summary>The seed values are derived from.</summary>
    public int Seed { get; }

    /// <summary>How many levels of required principals are built before giving up.</summary>
    /// <remarks>
    ///     A required self-reference, or a cycle of required references, has no finite graph that
    ///     satisfies it; past this depth the reference is left unset for the test to supply.
    /// </remarks>
    public int MaxDepth { get; }

    /// <summary>Builds a <typeparamref name="TEntity" /> with every required property set. Nothing is saved.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <param name="context">The context whose model describes the entity.</param>
    /// <param name="customize">Applied last, to set the values the test cares about.</param>
    /// <param name="fillOptional">Also fill nullable properties and optional owned references.</param>
    /// <returns>The entity, with its required principals attached.</returns>
    public TEntity Build<TEntity>(DbContext context, Action<TEntity>? customize = null, bool fillOptional = false)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException(
                $"Cannot build '{typeof(TEntity).Name}': it is not an entity type in '{context.GetType().Name}'.");

        if (typeof(TEntity).IsAbstract)
        {
            throw new InvalidOperationException(
                $"Cannot build '{typeof(TEntity).Name}': it is abstract. Build one of its derived types.");
        }

        lock (_lock)
        {
            var built = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var entity = (TEntity)BuildEntity(entityType, 0, fillOptional, built);

            if (customize is not null)
            {
                customize(entity);
                DropReplacedPrincipals(entity, entityType, built);
            }

            return entity;
        }
    }

    /// <summary>Builds <paramref name="count" /> entities. Nothing is saved.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <param name="context">The context whose model describes the entity.</param>
    /// <param name="count">How many to build.</param>
    /// <param name="customize">Applied last to each, with its zero-based index.</param>
    /// <param name="fillOptional">Also fill nullable properties and optional owned references.</param>
    /// <returns>The entities.</returns>
    public IReadOnlyList<TEntity> BuildMany<TEntity>(
        DbContext context,
        int count,
        Action<TEntity, int>? customize = null,
        bool fillOptional = false)
        where TEntity : class
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var entities = new List<TEntity>(count);
        for (var i = 0; i < count; i++)
        {
            var index = i;
            entities.Add(Build<TEntity>(context, customize is null ? null : e => customize(e, index), fillOptional));
        }

        return entities;
    }

    /// <summary>Builds a <typeparamref name="TEntity" /> and inserts it with its principals.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <param name="context">The context to insert through. Its change tracker is cleared afterwards.</param>
    /// <param name="customize">Applied last, to set the values the test cares about.</param>
    /// <param name="fillOptional">Also fill nullable properties and optional owned references.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The entity, with store-generated keys populated.</returns>
    public Task<TEntity> CreateAsync<TEntity>(
        DbContext context,
        Action<TEntity>? customize = null,
        bool fillOptional = false,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => context.SeedOneAsync(Build(context, customize, fillOptional), cancellationToken);

    /// <summary>Builds <paramref name="count" /> entities and inserts them with their principals.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <param name="context">The context to insert through. Its change tracker is cleared afterwards.</param>
    /// <param name="count">How many to build.</param>
    /// <param name="customize">Applied last to each, with its zero-based index.</param>
    /// <param name="fillOptional">Also fill nullable properties and optional owned references.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The entities, with store-generated keys populated.</returns>
    public Task<IReadOnlyList<TEntity>> CreateManyAsync<TEntity>(
        DbContext context,
        int count,
        Action<TEntity, int>? customize = null,
        bool fillOptional = false,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => context.SeedAsync(BuildMany(context, count, customize, fillOptional), cancellationToken);

    /// <summary>The builder for <paramref name="context" />: its test database's, or <see cref="Shared" />.</summary>
    internal static TestData For(DbContext context)
        => ByContext.TryGetValue(context, out var data) || ByOptions.TryGetValue(context.GetService<IDbContextOptions>(), out data)
            ? data
            : Shared;

    /// <summary>Makes <paramref name="data" /> the builder for <paramref name="context" />, whatever its options became.</summary>
    internal static void Associate(DbContext context, TestData data) => ByContext.AddOrUpdate(context, data);

    /// <summary>Makes <paramref name="data" /> the builder for every context created with <paramref name="options" />.</summary>
    internal static void Register(IDbContextOptions options, TestData data) => ByOptions.AddOrUpdate(options, data);

    private object BuildEntity(IEntityType entityType, int depth, bool fillOptional, HashSet<object> built)
    {
        var instance = Instantiate(entityType.ClrType);
        var discriminator = entityType.FindDiscriminatorProperty();

        var outer = _filling;
        _filling = entityType;
        try
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property == discriminator
                    || property.IsForeignKey()
                    || property.ValueGenerated != ValueGenerated.Never)
                {
                    continue;
                }

                FillProperty(instance, property, fillOptional);
            }

            FillComplexProperties(instance, entityType, fillOptional);
        }
        finally
        {
            _filling = outer;
        }

        if (depth < MaxDepth)
        {
            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                if (!foreignKey.IsRequired
                    || foreignKey.IsOwnership
                    || foreignKey.DependentToPrincipal is not { } navigation
                    || !CanSet(navigation)
                    || ConcreteType(foreignKey.PrincipalEntityType) is not { } principalType)
                {
                    continue;
                }

                var principal = BuildEntity(principalType, depth + 1, fillOptional, built);
                SetValue(instance, navigation, principal);
                built.Add(principal);
            }
        }

        // Owned references belong to the owner rather than standing beside it, so they do not count
        // against the depth — an owner past MaxDepth would otherwise be saved without a required part.
        foreach (var navigation in entityType.GetNavigations())
        {
            if (!navigation.ForeignKey.IsOwnership
                || navigation.IsOnDependent
                || navigation.IsCollection
                || !CanSet(navigation)
                || !(navigation.ForeignKey.IsRequiredDependent || fillOptional))
            {
                continue;
            }

            SetValue(instance, navigation, BuildEntity(navigation.TargetEntityType, depth, fillOptional, built));
        }

        return instance;
    }

    /// <summary>
    ///     A generated principal is dropped again when the test set the foreign key itself. Left
    ///     attached, it would be inserted and EF would overwrite the key the test chose with the new
    ///     principal's, so <c>o => o.CustomerId = alice.Id</c> would silently not mean Alice. A
    ///     principal the test attached itself is never dropped. Owned references are checked the same way.
    /// </summary>
    private static void DropReplacedPrincipals(object entity, IEntityType entityType, HashSet<object> built)
    {
        foreach (var foreignKey in entityType.GetForeignKeys())
        {
            if (foreignKey.IsOwnership
                || foreignKey.DependentToPrincipal is not { } navigation
                || !CanSet(navigation)
                || GetValue(entity, navigation) is not { } principal
                || !built.Contains(principal))
            {
                continue;
            }

            var keySet = foreignKey.Properties.Any(p =>
                !p.IsShadowProperty()
                && GetValue(entity, p) is var value
                && value is not null
                && !value.Equals(DefaultOf(p.ClrType)));

            if (keySet)
            {
                SetValue(entity, navigation, null);
            }
        }

        // An owned reference was built with principals of its own, and the test can set its keys too.
        foreach (var navigation in entityType.GetNavigations())
        {
            if (navigation.ForeignKey.IsOwnership
                && !navigation.IsOnDependent
                && !navigation.IsCollection
                && GetValue(entity, navigation) is { } owned)
            {
                DropReplacedPrincipals(owned, navigation.TargetEntityType, built);
            }
        }
    }

    private void FillComplexProperties(object instance, ITypeBase type, bool fillOptional)
    {
        foreach (var complexProperty in type.GetComplexProperties())
        {
            if (complexProperty.IsCollection
                || (complexProperty.IsNullable && !fillOptional)
                || !CanSet(complexProperty))
            {
                continue;
            }

            var complexType = complexProperty.ComplexType;
            var value = Instantiate(complexType.ClrType);

            foreach (var property in complexType.GetProperties())
            {
                if (property.ValueGenerated == ValueGenerated.Never)
                {
                    FillProperty(value, property, fillOptional);
                }
            }

            FillComplexProperties(value, complexType, fillOptional);

            // A struct is filled boxed, then assigned: the box is the copy that gets the values.
            SetValue(instance, complexProperty, value);
        }
    }

    private void FillProperty(object instance, IProperty property, bool fillOptional)
    {
        if ((property.IsNullable && !fillOptional) || property.IsShadowProperty() || !CanSet(property))
        {
            return;
        }

        var value = property.IsPrimitiveCollection
            ? GenerateCollection(property)
            : Generate(Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType, property);

        // Unsupported types are left as they are — the CLR default, or whatever the constructor set.
        if (value is not null)
        {
            SetValue(instance, property, value);
        }
    }

    private object? GenerateCollection(IProperty property)
    {
        var elementClrType = property.GetElementType()?.ClrType;
        if (elementClrType is null)
        {
            return null;
        }

        var underlying = Nullable.GetUnderlyingType(elementClrType) ?? elementClrType;
        // The element's store type is the column's without its [] — timestamp without time zone[] holds
        // timestamp without time zone values, whose DateTime kind has to match.
        var element = Generate(underlying, null, property.GetColumnType() is { } column && column.EndsWith("[]", StringComparison.Ordinal) ? column[..^2] : null);
        if (element is null)
        {
            return null;
        }

        var collectionType = property.ClrType;
        if (collectionType.IsArray)
        {
            var array = Array.CreateInstance(elementClrType, 1);
            array.SetValue(element, 0);
            return array;
        }

        var listType = typeof(List<>).MakeGenericType(elementClrType);
        if (collectionType.IsAssignableFrom(listType))
        {
            var list = (IList)Activator.CreateInstance(listType)!;
            list.Add(element);
            return list;
        }

        return null;
    }

    /// <summary>A unique, valid value of <paramref name="type" />, or <see langword="null" /> if unsupported.</summary>
    /// <param name="type">The non-nullable CLR type.</param>
    /// <param name="property">The property, for its facets, if there is one.</param>
    /// <param name="storeType">The store type when there is no property — a collection's element.</param>
    private object? Generate(Type type, IProperty? property, string? storeType = null)
    {
        var n = ++_counter;

        if (type == typeof(string))
        {
            return GenerateString(property?.Name ?? "Value", n, MaxLength(property));
        }

        // Neither an enum nor a bool can be unique, and stepping through them with the counter would
        // land on the same member whenever an entity has a multiple of that many values — so these
        // are drawn from the seeded generator instead, which varies them and stays deterministic.
        if (type.IsEnum)
        {
            var values = Enum.GetValuesAsUnderlyingType(type);
            return values.Length == 0
                ? null
                : Enum.ToObject(type, values.GetValue(_random.Next(values.Length))!);
        }

        return type switch
        {
            _ when type == typeof(bool) => _random.Next(2) == 0,
            // Every value of the type, the positive ones first: 1, 2, … 127, then -128 … -1, then 0.
            _ when type == typeof(byte) => unchecked((byte)Bounded(n, property)),
            _ when type == typeof(sbyte) => unchecked((sbyte)Bounded(n, property)),
            _ when type == typeof(short) => unchecked((short)Bounded(n, property)),
            _ when type == typeof(ushort) => unchecked((ushort)Bounded(n, property)),
            _ when type == typeof(int) => (int)(n % int.MaxValue),
            _ when type == typeof(uint) => (uint)(n % uint.MaxValue),
            _ when type == typeof(long) => n,
            _ when type == typeof(ulong) => (ulong)n,
            _ when type == typeof(float) => (float)n,
            _ when type == typeof(double) => (double)n,
            _ when type == typeof(decimal) => GenerateDecimal(Bounded(n, property), property),
            _ when type == typeof(char) => (char)('a' + (n % 26)),
            _ when type == typeof(Guid) => GenerateGuid(),
            _ when type == typeof(DateTime) => GenerateDateTime(n, storeType ?? property?.GetColumnType()),
            _ when type == typeof(DateTimeOffset) => new DateTimeOffset(BaseDate.AddMinutes(n % MaxMinutes)),
            _ when type == typeof(DateOnly) => DateOnly.FromDateTime(BaseDate).AddDays((int)(n % MaxDays)),
            _ when type == typeof(TimeOnly) => new TimeOnly(n % SecondsPerDay * TimeSpan.TicksPerSecond),
            _ when type == typeof(TimeSpan) => TimeSpan.FromSeconds(n % SecondsPerDay),
            _ when type == typeof(byte[]) => GenerateBytes(n, MaxLength(property)),
            _ => null,
        };
    }

    /// <summary>The column's own count, for a value that wraps; the shared counter where there is no column.</summary>
    private long Bounded(long n, IProperty? property)
    {
        if (property is null)
        {
            return n;
        }

        var column = property.IsPrimaryKey()
            ? null
            : _filling?.GetTableMappings().SelectMany(m => m.ColumnMappings).FirstOrDefault(c => c.Property == property)?.Column;
        var key = column is not null
            ? ((object)(column.Table.Schema, column.Table.Name), column.Name)
            : (property.DeclaringType.ContainingEntityType.GetRootType(), property.GetColumnName());
        var next = _boundedCounters.GetValueOrDefault(key) + 1;
        _boundedCounters[key] = next;
        return next;
    }

    // Up to the year 2999: inside every engine's date range, and still unique for a million values.
    private const long MaxDays = 365L * 999;
    private const long MaxMinutes = MaxDays * 24 * 60;
    private const long SecondsPerDay = 24 * 60 * 60;

    // HasColumnType("varchar(4)") sets no max-length facet; only the resolved type mapping's Size knows
    // it. A size of zero or less is that mapping's "unbounded" (max).
    private static int? MaxLength(IProperty? property)
        => property?.GetMaxLength() ?? (property?.FindRelationalTypeMapping()?.Size is > 0 and var size ? size : null);

    /// <remarks>
    ///     The counter goes at the end and the property name is cut to make room for it, so a string
    ///     stays unique however short its column is: uniqueness is lost only once the counter itself
    ///     no longer fits. A bounded column always gets the counter in base 36, which puts that off — and
    ///     only ever in base 36: decimal where it fits and base 36 where not wrote 30 for both 30 and 108.
    /// </remarks>
    private static string GenerateString(string name, long n, int? maxLength)
    {
        if (maxLength is not { } max)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{name}-{n}");
        }

        var suffix = ToBase36(n);
        var full = $"{name}-{suffix}";
        if (full.Length <= max)
        {
            return full;
        }

        if (suffix.Length >= max)
        {
            return suffix[^max..];
        }

        var prefixLength = max - suffix.Length - 1;
        return prefixLength <= 0 ? suffix : $"{name[..Math.Min(prefixLength, name.Length)]}-{suffix}";
    }

    private static string ToBase36(long value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[16];
        var position = buffer.Length;
        do
        {
            buffer[--position] = digits[(int)(value % 36)];
            value /= 36;
        }
        while (value > 0);

        return new string(buffer[position..]);
    }

    /// <remarks>
    ///     Kept inside the column's digits, fractional ones included, so <c>decimal(5,2)</c> never overflows
    ///     and <c>decimal(2,2)</c>, with no integer digits at all, still gets 100 distinct values rather than
    ///     always 0. A column holds only so many: past 10^precision values they repeat. Wide ones too:
    ///     at most 18 digits are drawn, which a long holds, and a CLR decimal's scale stops at 28 — so
    ///     <c>numeric(18,18)</c> gets fractions below 1 rather than integers it would refuse.
    /// </remarks>
    private static decimal GenerateDecimal(long n, IProperty? property)
    {
        // HasColumnType("numeric(2,2)") sets no precision facet; only the resolved type mapping knows it.
        var mapping = property?.FindRelationalTypeMapping();
        if ((property?.GetPrecision() ?? mapping?.Precision) is not { } precision)
        {
            return n;
        }

        var digits = Math.Clamp(precision, 1, 18);

        // PostgreSQL's numeric(2,-3) rounds to thousands, so 1 and 2 are both stored as 0: step by the
        // rounding unit instead. A CLR decimal holds 28 digits, so the step keeps the column's unit and
        // the counter gives up digits; a unit past 10^27 is out of a decimal's reach altogether.
        if (NegativeScale(property!.GetColumnType()) is { } rounding)
        {
            var places = Math.Min(rounding, 27);
            return n % (long)Pow10(Math.Clamp(28 - places, 1, digits)) * Pow10(places);
        }

        var scale = Math.Clamp(property!.GetScale() ?? mapping?.Scale ?? 0, 0, 28);
        return n % (long)Pow10(digits) / Pow10(scale);
    }

    /// <summary>How many places a column like <c>numeric(2,-3)</c> rounds to the left of the point.</summary>
    private static int? NegativeScale(string? columnType)
        => columnType is not null
            && System.Text.RegularExpressions.Regex.Match(columnType, @"\(\s*\d+\s*,\s*-\s*(\d+)\s*\)") is { Success: true } match
            && int.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var places)
            && places > 0
                ? places
                : null;

    private static decimal Pow10(int exponent)
    {
        var value = 1m;
        for (var i = 0; i < exponent; i++)
        {
            value *= 10m;
        }

        return value;
    }

    private Guid GenerateGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        _random.NextBytes(bytes);
        return new Guid(bytes);
    }

    /// <remarks>
    ///     UTC, which is what Npgsql demands for <c>timestamp with time zone</c> — its default for
    ///     <see cref="DateTime" /> — and what the other engines ignore. A column declared without a time
    ///     zone refuses UTC on Npgsql instead, so there the kind is left unspecified.
    /// </remarks>
    private static DateTime GenerateDateTime(long n, string? storeType)
    {
        // Without its precision, so timestamptz(3) is still the time-zone alias and timestamp(3) without
        // time zone still the plain one.
        var columnType = storeType is { } declared
            ? System.Text.RegularExpressions.Regex.Replace(declared, @"\s*\(\s*\d+\s*\)", "").Trim().ToLowerInvariant()
            : null;

        // A date column keeps only the day: values a minute apart would be one value there, and a unique
        // index on it would refuse the second row.
        var value = columnType == "date"
            ? BaseDate.AddDays(n % MaxDays)
            : BaseDate.AddMinutes(n % MaxMinutes);
        return columnType is not null
            && columnType.StartsWith("timestamp", StringComparison.Ordinal)
            && !columnType.Contains("with time zone", StringComparison.Ordinal)
            && columnType != "timestamptz"
            ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
            : value;
    }

    private static byte[] GenerateBytes(long n, int? maxLength)
    {
        var bytes = BitConverter.GetBytes(n);
        return maxLength is { } max && max < bytes.Length ? bytes[..max] : bytes;
    }

    private static IEntityType? ConcreteType(IEntityType entityType)
        => entityType.GetDerivedTypesInclusive().FirstOrDefault(t => !t.ClrType.IsAbstract);

    private static object Instantiate(Type type)
    {
        if (type.IsValueType)
        {
            return Activator.CreateInstance(type)!;
        }

        // A parameterless constructor runs the type's own initialisers — collection navigations
        // created empty, say. Without one, EF's constructor binding is the only other way in, and
        // an uninitialised object is what it would start from too.
        return type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is { } constructor
            ? constructor.Invoke(null)
            : RuntimeHelpers.GetUninitializedObject(type);
    }

    private static bool CanSet(IPropertyBase property)
        => property.PropertyInfo?.SetMethod is not null || property.FieldInfo is not null;

    private static void SetValue(object instance, IPropertyBase property, object? value)
    {
        if (property.PropertyInfo is { SetMethod: not null } propertyInfo)
        {
            propertyInfo.SetValue(instance, value);
        }
        else
        {
            property.FieldInfo!.SetValue(instance, value);
        }
    }

    private static object? GetValue(object instance, IPropertyBase property)
        => property.PropertyInfo is { GetMethod: not null } propertyInfo
            ? propertyInfo.GetValue(instance)
            : property.FieldInfo?.GetValue(instance);

    private static object? DefaultOf(Type type)
        => type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;
}
