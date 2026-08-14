using System.Linq.Expressions;
using EFToolkit.Bulk.Execution;
using EFToolkit.Bulk.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Bulk.Api;

/// <summary>
///     How to read and write one entity type's columns, compiled once and reused.
/// </summary>
/// <remarks>
///     <para>
///         The explicit bulk API's whole advantage is that it does not build modification commands,
///         so it must get at property values some other way. Reflection per property per row would
///         give the saving straight back, so accessors are compiled to delegates once per entity
///         type and cached against the model — which EF treats as immutable and long-lived.
///     </para>
///     <para>
///         "Against the model" is literal: the plan and its accessors are runtime annotations on
///         the metadata they were derived from, so they live and die with it. See
///         <see cref="BulkAnnotations" /> for why that matters.
///     </para>
/// </remarks>
internal sealed class BulkEntityPlan
{
    private BulkEntityPlan(
        Type entityClrType,
        string tableName,
        string? schema,
        IReadOnlyList<BulkColumnInfo> columns,
        Func<object, object?>[] getters,
        Action<object, object?>?[] setters)
    {
        EntityClrType = entityClrType;
        TableName = tableName;
        Schema = schema;
        Columns = columns;
        Getters = getters;
        Setters = setters;
    }

    /// <summary>CLR type of the entities this plan reads.</summary>
    public Type EntityClrType { get; }

    public string TableName { get; }
    public string? Schema { get; }
    public IReadOnlyList<BulkColumnInfo> Columns { get; }
    public Func<object, object?>[] Getters { get; }
    public Action<object, object?>?[] Setters { get; }

    /// <summary>
    ///     Gets, or builds and caches, the plan for <paramref name="entityType" /> in
    ///     <paramref name="state" />.
    /// </summary>
    /// <remarks>
    ///     The plan is state-specific because a column's role changes with the operation. A
    ///     generated key is read back on insert, but on update or delete it is what locates the
    ///     row; and a computed column that must be read after an insert cannot be written at all.
    /// </remarks>
    public static BulkEntityPlan For(IEntityType entityType, EntityState state)
        => entityType.GetOrAddRuntimeAnnotationValue(
            BulkAnnotations.Plan(state),
            static key => Build(key.EntityType, key.State, null, null),
            (EntityType: entityType, State: state));

    /// <summary>
    ///     Builds a plan for a call that narrowed what the operation matches on or writes.
    /// </summary>
    /// <remarks>
    ///     Uncached whenever either is present: both vary per call, and folding them into the cache
    ///     key would not work anyway — record equality over an <c>IReadOnlyList</c> is reference
    ///     equality, so the entry would silently never be hit. These are explicit, comparatively
    ///     rare operations where building the plan is dwarfed by the write itself, and the
    ///     per-property accessors are cached independently.
    /// </remarks>
    /// <param name="entityType">The entity type being written.</param>
    /// <param name="state">What is being done to the rows.</param>
    /// <param name="matchProperties">
    ///     The columns that locate the row, or <see langword="null" /> for the primary key.
    /// </param>
    /// <param name="projection">The narrowed write set, or <see langword="null" /> for all of it.</param>
    public static BulkEntityPlan For(
        IEntityType entityType,
        EntityState state,
        IReadOnlyList<IProperty>? matchProperties,
        BulkProjection? projection)
        => matchProperties is null && projection is null
            ? For(entityType, state)
            : Build(entityType, state, matchProperties, projection);

    private static BulkEntityPlan Build(
        IEntityType entityType,
        EntityState state,
        IReadOnlyList<IProperty>? matchProperties,
        BulkProjection? projection)
    {
        var mapping = entityType.GetTableMappings().FirstOrDefault()
            ?? throw new BulkNotSupportedException(
                $"'{entityType.DisplayName()}' is not mapped to a table, so it cannot be "
                + "bulk-inserted.");

        var table = mapping.Table;
        var columns = new List<BulkColumnInfo>();
        var getters = new List<Func<object, object?>>();
        var setters = new List<Action<object, object?>?>();

        foreach (var columnMapping in mapping.ColumnMappings)
        {
            var property = columnMapping.Property;
            var column = columnMapping.Column;

            if (property.PropertyInfo is null && property.FieldInfo is null)
            {
                // A shadow property has no CLR member to read, and the explicit API works from
                // detached objects with no entry to read it from either.
                throw new BulkNotSupportedException(
                    $"'{entityType.DisplayName()}.{property.Name}' is a shadow property. The "
                    + "explicit bulk API reads values from the entities themselves, so it cannot "
                    + "supply one. Use SaveChanges() for this entity type.");
            }

            var isStoreGenerated = IsStoreGenerated(property);
            var isKey = property.IsKey();

            bool isWrite, isRead, isCondition;

            // Only a merge or a synchronise pairs match columns with Added: an insert has nothing
            // to match, so match columns on this state can only have come from one of those.
            if (matchProperties is not null && state == EntityState.Added)
            {
                // Merge. The match columns locate the row and are also written, so a row that does
                // not exist yet can be inserted with them. A store-generated key is read back,
                // because only the rows that turned out to be inserts will have one.
                isCondition = matchProperties.Any(p => p.Name == property.Name);
                isRead = isStoreGenerated;
                isWrite = Project(
                    projection, entityType, property, !isStoreGenerated, isKey, isCondition);

                if (!isWrite && !isRead && !isCondition)
                {
                    continue;
                }

                columns.Add(new BulkColumnInfo(
                    column.Name, column.StoreTypeMapping, property,
                    isWrite, isRead, isKey, isCondition,
                    isWrite && projection?.IsInsertOnly(property) == true));

                getters.Add(Getter(property));
                setters.Add(isRead ? Setter(property) : null);
                continue;
            }

            switch (state)
            {
                case EntityState.Added:
                    isCondition = false;
                    isRead = isStoreGenerated;
                    isWrite = Project(
                        projection, entityType, property, !isStoreGenerated, isKey, isCondition);
                    break;

                case EntityState.Modified when property.IsConcurrencyToken:
                    // Stock EF locates the row with the token's *loaded* value while assigning the
                    // new one, which needs a before-image. A detached entity has none: whatever the
                    // token holds now is all there is, and for the usual pattern -- load, increment,
                    // save -- that is already the new value, so a join on it would match nothing.
                    //
                    // Continuing without the token in the WHERE clause is the one option that must
                    // not be taken. It silently turns an optimistic-concurrency check into
                    // last-writer-wins, which is a data-loss bug that looks like a working call.
                    throw new BulkNotSupportedException(
                        $"'{entityType.DisplayName()}.{property.Name}' is a concurrency token, and "
                        + "the explicit bulk API works from detached objects that carry no "
                        + "before-image to check it against. Use SaveChanges() for this entity "
                        + "type, which tracks the loaded value and can.");

                case EntityState.Modified:
                    // The match columns locate the row -- the key unless the caller named others --
                    // and everything else the application owns is set. A store-generated non-key
                    // column is left alone entirely: it is the database's to maintain.
                    //
                    // A key stays out of the write set even when it is not a match column. It is
                    // still what identifies the row, and reassigning it would turn an update into a
                    // silent re-key.
                    isCondition = Matches(matchProperties, property, isKey);
                    isRead = false;
                    isWrite = Project(
                        projection,
                        entityType,
                        property,
                        !isKey && !isCondition && !isStoreGenerated,
                        isKey,
                        isCondition);
                    break;

                case EntityState.Deleted:
                    isCondition = Matches(matchProperties, property, isKey);
                    isRead = false;
                    isWrite = false;
                    break;

                default:
                    throw new BulkNotSupportedException(
                        $"{state} has no bulk equivalent for '{entityType.DisplayName()}'.");
            }

            if (!isWrite && !isRead && !isCondition)
            {
                // Nothing to do with this column in this operation, so it does not need an
                // accessor either.
                continue;
            }

            columns.Add(new BulkColumnInfo(
                column.Name,
                column.StoreTypeMapping,
                property,
                isWrite,
                isRead,
                isKey,
                isCondition));

            getters.Add(Getter(property));
            setters.Add(isRead ? Setter(property) : null);
        }

        if (projection is not null && state == EntityState.Modified && !columns.Any(c => c.IsWrite))
        {
            // Left to run, this reaches the executor's "no columns to set" decline and falls back
            // to stock EF -- which writes every column, the exact opposite of what the projection
            // asked for. Better to say so than to do the opposite quietly.
            throw new BulkNotSupportedException(
                $"An update of '{entityType.DisplayName()}' has no columns left to write after "
                + $"{projection.Describe()}. Every remaining column either locates the row or is "
                + "the database's to maintain.");
        }

        return new BulkEntityPlan(
            entityType.ClrType,
            table.Name, table.Schema, columns, [.. getters], [.. setters]);
    }

    /// <summary>Whether <paramref name="property" /> is one of the columns that locates the row.</summary>
    /// <remarks>
    ///     Matched by name rather than by reference: an inherited property is one <c>IProperty</c>
    ///     for the whole hierarchy, and a selector resolved against a sibling type would otherwise
    ///     miss it.
    /// </remarks>
    private static bool Matches(
        IReadOnlyList<IProperty>? matchProperties,
        IProperty property,
        bool isKey)
        => matchProperties is null
            ? isKey
            : matchProperties.Any(p => p.Name == property.Name);

    /// <summary>
    ///     Applies the caller's projection to a column that would otherwise be written.
    /// </summary>
    /// <remarks>
    ///     A projection narrows what gets written, never what locates the row. Dropping a key or a
    ///     match column would leave the statement joining on a column it never staged, or inserting
    ///     a row with no key at all — so that is refused rather than accommodated.
    /// </remarks>
    private static bool Project(
        BulkProjection? projection,
        IEntityType entityType,
        IProperty property,
        bool isWrite,
        bool isKey,
        bool isCondition)
    {
        if (projection is null)
        {
            return isWrite;
        }

        var projected = projection.Writes(property, isWrite);

        if (projected == isWrite || !(isKey || isCondition))
        {
            return projected;
        }

        throw new BulkNotSupportedException(
            $"'{entityType.DisplayName()}.{property.Name}' is "
            + (isKey ? "part of the key" : "one of the match columns")
            + $", so it cannot be left out of the write set by {projection.Describe()}. It is what "
            + "identifies the row, and the operation has no way to locate one without it.");
    }

    /// <summary>
    ///     Whether the database produces this property's value, rather than the application.
    /// </summary>
    /// <remarks>
    ///     Computed columns and columns with a database default are generated on insert. Keys
    ///     configured as value-generated are too — unless EF would have generated them client-side
    ///     (a <c>Guid</c> key, or HiLo), in which case the value is already on the entity and is
    ///     written like any other.
    /// </remarks>
    private static bool IsStoreGenerated(IProperty property)
    {
        if (property.GetComputedColumnSql() is not null)
        {
            return true;
        }

        if (!property.ValueGenerated.HasFlag(ValueGenerated.OnAdd))
        {
            return false;
        }

        // A configured client-side factory means the application supplies the value.
        return property.GetValueGeneratorFactory() is null;
    }

    /// <summary>Gets, or compiles and caches, the getter for <paramref name="property" />.</summary>
    /// <remarks>
    ///     Cached on the property rather than on the plan that asked for it. Compiling an
    ///     expression tree is expensive and a property's accessor never varies, while a plan built
    ///     for explicit match columns or a projection cannot be cached at all — both vary per call
    ///     — and so was compiling one or two trees per column on every single merge: roughly sixty
    ///     compilations for a thirty-column entity, every time.
    /// </remarks>
    private static Func<object, object?> Getter(IProperty property)
        => property.GetOrAddRuntimeAnnotationValue(
            BulkAnnotations.Getter, static p => BuildGetter(p!), property);

    /// <summary>
    ///     Gets, or compiles and caches, the setter for <paramref name="property" />, or
    ///     <see langword="null" /> when it has no writable member.
    /// </summary>
    private static Action<object, object?>? Setter(IProperty property)
        => property.GetOrAddRuntimeAnnotationValue(
            BulkAnnotations.Setter, static p => BuildSetter(p!), property);

    /// <summary>Compiles a delegate that reads <paramref name="property" /> off an entity.</summary>
    /// <remarks>
    ///     The cast target is the property's declaring type, not the entity type whose plan asked
    ///     for the accessor. An inherited property is one <c>IProperty</c> shared by every type in
    ///     the hierarchy, so an accessor cached against it has to be valid for all of them; casting
    ///     to whichever type happened to be planned first would hand a sibling an accessor that
    ///     casts to the wrong CLR type. The declaring type is valid for every instance carrying the
    ///     member, which is exactly the set the cache entry covers.
    /// </remarks>
    private static Func<object, object?> BuildGetter(IProperty property)
    {
        var parameter = Expression.Parameter(typeof(object), "entity");
        var typed = Expression.Convert(parameter, property.DeclaringType.ClrType);
        var access = Access(typed, property);

        return Expression
            .Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), parameter)
            .Compile();
    }

    private static Action<object, object?>? BuildSetter(IProperty property)
    {
        // Prefer the backing field: a store-generated key is commonly exposed through a property
        // with no public setter, and EF writes such values through the field too.
        var writableField = property.FieldInfo is { IsInitOnly: false };

        if (!writableField && property.PropertyInfo is not { CanWrite: true })
        {
            return null;
        }

        var entity = Expression.Parameter(typeof(object), "entity");
        var value = Expression.Parameter(typeof(object), "value");
        var typed = Expression.Convert(entity, property.DeclaringType.ClrType);

        var target = writableField
            ? Expression.Field(typed, property.FieldInfo!)
            : Expression.Property(typed, property.PropertyInfo!);

        var assign = Expression.Assign(target, Expression.Convert(value, target.Type));

        return Expression.Lambda<Action<object, object?>>(assign, entity, value).Compile();
    }

    private static MemberExpression Access(Expression target, IProperty property)
        => property.PropertyInfo is not null
            ? Expression.Property(target, property.PropertyInfo)
            : Expression.Field(target, property.FieldInfo!);
}
