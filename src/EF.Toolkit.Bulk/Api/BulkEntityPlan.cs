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
        var mappings = entityType.GetTableMappings().ToList();

        if (mappings.Count == 0)
        {
            throw new BulkNotSupportedException(
                $"'{entityType.DisplayName()}' is not mapped to a table, so it cannot be "
                + "bulk-inserted.");
        }

        RefuseMoreThanOneTable(entityType, mappings);

        var mapping = mappings[0];
        var table = mapping.Table;

        RefuseASharedTable(entityType, table);
        RefuseAPropertyBag(entityType);
        RefuseAKeylessType(entityType, state, matchProperties);

        var columns = new List<BulkColumnInfo>();
        var getters = new List<Func<object, object?>>();
        var setters = new List<Action<object, object?>?>();

        // Flattened rather than the table's column mappings, because a complex property's columns
        // sit on this table but are mapped by the complex type rather than by the entity: reading
        // the entity's own mappings wrote every other column and left those to the table's
        // defaults. Flattening also reaches arbitrarily deep, which is how Money.Audit.By finds its
        // Money_Audit_By column.
        foreach (var property in entityType.GetFlattenedProperties())
        {
            var column = property.GetTableColumnMappings()
                .FirstOrDefault(m => m.TableMapping.Table == table)?.Column;

            if (column is null)
            {
                // Mapped to something other than this table. Nothing this statement can do with it.
                continue;
            }

            if (property.PropertyInfo is null && property.FieldInfo is null)
            {
                RefuseAShadowProperty(entityType, property, table);
            }

            var isStoreGenerated = IsStoreGenerated(property);

            // Primary key, not any key. IProperty.IsKey() is also true for an alternate key, and
            // treating one as a row locator put it in the WHERE clause and kept it out of the SET
            // clause -- so an update whose alternate key had changed matched nothing, and the
            // column could never be written at all. Only the primary key identifies the row.
            var isKey = property.IsPrimaryKey();

            if (property.IsConcurrencyToken)
            {
                RefuseAConcurrencyToken(entityType, property, state, matchProperties);
            }

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

    /// <summary>
    ///     Refuses an entity type whose rows live in more than one table.
    /// </summary>
    /// <remarks>
    ///     Table-per-type and entity splitting both map one entity type across several tables, and a
    ///     row is only complete once every one of them has been written. A bulk operation writes one
    ///     table, so taking the first mapping and proceeding wrote the base table and silently
    ///     dropped everything the derived or split table held — an insert that reported success and
    ///     left half the entity missing. There is no partial answer worth giving here.
    /// </remarks>
    private static void RefuseMoreThanOneTable(
        IEntityType entityType,
        List<ITableMapping> mappings)
    {
        if (mappings.Count <= 1)
        {
            return;
        }

        var tables = string.Join(", ", mappings.Select(m => $"'{m.Table.Name}'"));

        throw new BulkNotSupportedException(
            $"'{entityType.DisplayName()}' is mapped to more than one table ({tables}), which "
            + "table-per-type inheritance and entity splitting both do. One row spans all of them, "
            + "and a bulk statement writes one table, so writing only the first would leave the "
            + "rest of the entity behind. Use SaveChanges() for this entity type, which writes "
            + "every table the row occupies. EF.Toolkit.Bulk still accelerates that save.");
    }

    /// <summary>
    ///     Refuses an entity type that shares its table with an entity type outside its hierarchy.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An owned reference, an owned type mapped to JSON and explicit table splitting all put
    ///         a second entity type's columns on this type's table — and those columns are absent
    ///         from this type's own <c>ColumnMappings</c>. The plan is built from those mappings, so
    ///         the write went ahead with the sharer's columns silently omitted: the owner's own
    ///         columns landed, the owned ones were left to whatever the table's defaults gave them,
    ///         and the call reported success.
    ///     </para>
    ///     <para>
    ///         Sharers are compared by root type, which is what keeps table-per-hierarchy working.
    ///         Every type in a TPH hierarchy maps to the one table and shares a root, and each
    ///         derived type's own mappings already cover the columns it declares — so a hierarchy is
    ///         not table sharing in the sense that matters here.
    ///     </para>
    /// </remarks>
    private static void RefuseASharedTable(IEntityType entityType, ITable table)
    {
        var root = entityType.GetRootType();

        foreach (var mapping in table.EntityTypeMappings)
        {
            if (mapping.TypeBase is not IEntityType sharer || sharer.GetRootType() == root)
            {
                continue;
            }

            var how = sharer.IsMappedToJson()
                ? "is mapped to a JSON column on it"
                : sharer.IsOwned()
                    ? "is owned by it and shares it"
                    : "is split across it";

            throw new BulkNotSupportedException(
                $"'{entityType.DisplayName()}' shares table '{table.Name}' with "
                + $"'{sharer.DisplayName()}', which {how}. The explicit bulk API writes the columns "
                + $"'{entityType.DisplayName()}' maps itself, so the shared ones would be left "
                + "unwritten and the row would come out half-populated. Use SaveChanges() for this "
                + "entity type, which writes the whole row. EF.Toolkit.Bulk still accelerates that "
                + "save.");
        }
    }

    /// <summary>
    ///     Refuses a shadow property, naming the feature that introduced it where one did.
    /// </summary>
    /// <remarks>
    ///     A temporal table's period columns are shadow properties nobody declared, so the plain
    ///     message sent people looking for a shadow property they had not written. The refusal is
    ///     the same either way — there is no CLR member to read a value from — but a message that
    ///     names <c>IsTemporal()</c> is one someone can act on.
    /// </remarks>
    private static void RefuseAShadowProperty(
        IEntityType entityType,
        IProperty property,
        ITable table)
    {
        // Read as an annotation rather than through SqlServerEntityTypeExtensions.IsTemporal():
        // this assembly deliberately references only EF's relational layer, and taking a dependency
        // on the SQL Server provider to improve one message would be a poor trade.
        if (entityType.FindAnnotation("SqlServer:IsTemporal")?.Value is true)
        {
            throw new BulkNotSupportedException(
                $"'{entityType.DisplayName()}' is mapped to temporal table '{table.Name}'. Its "
                + $"period columns — '{property.Name}' among them — are shadow properties the "
                + "provider maintains, and the explicit bulk API reads values from the entities "
                + "themselves, so it cannot supply them. Use SaveChanges() for this entity type; "
                + "EF.Toolkit.Bulk still accelerates that save, and history is recorded as usual.");
        }

        throw new BulkNotSupportedException(
            $"'{entityType.DisplayName()}.{property.Name}' is a shadow property. The explicit bulk "
            + "API reads values from the entities themselves, so it cannot supply one. Use "
            + "SaveChanges() for this entity type.");
    }

    /// <summary>
    ///     Refuses an entity type with no CLR type of its own to read values from.
    /// </summary>
    /// <remarks>
    ///     The implicit join entity behind a skip navigation is the one that turns up in practice:
    ///     EF models it as a property bag over <c>Dictionary&lt;string, object&gt;</c>, whose
    ///     properties are indexer entries rather than members. The accessor compiler used to reach
    ///     those and fail with an <c>ArgumentException</c> about <c>get_Item</c> — accurate, and
    ///     useless to whoever called <c>BulkInsertAsync</c>.
    /// </remarks>
    private static void RefuseAPropertyBag(IEntityType entityType)
    {
        if (!entityType.IsPropertyBag)
        {
            return;
        }

        throw new BulkNotSupportedException(
            $"'{entityType.DisplayName()}' is a property-bag entity type, which has no CLR class "
            + "whose members the explicit bulk API could read. The usual source is the implicit "
            + "join entity behind a many-to-many skip navigation. Give the relationship an explicit "
            + "join entity with UsingEntity<T>() and bulk-write that, or use SaveChanges() — which "
            + "EF.Toolkit.Bulk accelerates either way.");
    }

    /// <summary>
    ///     Refuses an operation that needs to locate a row on a type that has no key to locate it by.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the most dangerous thing the planner could get wrong, and it did. A keyless
    ///         entity type contributes no key columns, so nothing became a condition: the update
    ///         went out with an empty <c>WHERE</c> clause and rewrote every row in the table, and
    ///         the delete had nothing to confine it either. Both reported success.
    ///     </para>
    ///     <para>
    ///         An insert is fine and stays allowed — it locates no row, which is exactly why the
    ///         missing key costs it nothing. A caller who does have something unique to match on can
    ///         name it with <c>MatchOn</c>, and that is honoured: the refusal is about there being
    ///         no locator at all, not about the key specifically.
    ///     </para>
    /// </remarks>
    private static void RefuseAKeylessType(
        IEntityType entityType,
        EntityState state,
        IReadOnlyList<IProperty>? matchProperties)
    {
        if (entityType.FindPrimaryKey() is not null || matchProperties is { Count: > 0 })
        {
            return;
        }

        if (state == EntityState.Added)
        {
            return;
        }

        var operation = state == EntityState.Deleted ? "a delete" : "an update";

        throw new BulkNotSupportedException(
            $"'{entityType.DisplayName()}' has no primary key, so {operation} has no way to "
            + "locate the rows it should affect — it would apply to every row in "
            + $"'{entityType.GetTableName()}'. Name the columns that identify a row with MatchOn, "
            + "or give the entity type a key. Keyless types are usually mapped to a view or a query "
            + "and are not writable at all.");
    }

    /// <summary>
    ///     Refuses a concurrency token on any operation that has to locate an existing row.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Stock EF puts the token's <em>loaded</em> value in the <c>WHERE</c> clause, which
    ///         needs a before-image. A detached entity has none: whatever the token holds now is all
    ///         there is, and for the usual pattern — load, increment, save — that is already the new
    ///         value, so a join on it would match nothing.
    ///     </para>
    ///     <para>
    ///         Continuing without the token in the <c>WHERE</c> clause is the one option that must
    ///         not be taken. It silently turns an optimistic-concurrency check into last-writer-wins,
    ///         which is a data-loss bug that looks like a working call. That reasoning is not
    ///         specific to an update: a delete and a merge's update arm locate an existing row on
    ///         exactly the same terms, and dropping the check there loses exactly as much.
    ///     </para>
    ///     <para>
    ///         A plain insert is the one operation that is fine. It looks for no row, so it has
    ///         nothing to check the token against — the token is written like any other column, which
    ///         is what stock EF does too.
    ///     </para>
    /// </remarks>
    private static void RefuseAConcurrencyToken(
        IEntityType entityType,
        IProperty property,
        EntityState state,
        IReadOnlyList<IProperty>? matchProperties)
    {
        // Added with match columns is a merge or a synchronise, whose update arm rewrites rows that
        // already exist. Added without them is an insert, which reaches no existing row at all.
        var locatesAnExistingRow = state != EntityState.Added || matchProperties is not null;

        if (!locatesAnExistingRow)
        {
            return;
        }

        var operation = state switch
        {
            EntityState.Modified => "An update",
            EntityState.Deleted => "A delete",
            _ => "A merge or synchronise",
        };

        throw new BulkNotSupportedException(
            $"'{entityType.DisplayName()}.{property.Name}' is a concurrency token, and the explicit "
            + "bulk API works from detached objects that carry no before-image to check it against. "
            + $"{operation} would locate the row without the token, quietly downgrading the check to "
            + "last-writer-wins. Use SaveChanges() for this entity type, which tracks the loaded "
            + "value and can.");
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
        var path = ComplexPath(property);
        var typed = Expression.Convert(parameter, Owner(property, path));

        Expression access = Access(Walk(typed, path), property);
        access = Expression.Convert(access, typeof(object));

        // Any complex property on the way in may be optional, and EF writes null to every column of
        // an absent one. Guarding turns that into the nulls the columns expect, rather than a
        // NullReferenceException in the middle of streaming a batch.
        if (Guard(typed, path) is { } reachable)
        {
            access = Expression.Condition(
                reachable, access, Expression.Constant(null, typeof(object)));
        }

        return Expression.Lambda<Func<object, object?>>(access, parameter).Compile();
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

        var path = ComplexPath(property);

        // Writing back into an optional complex value would have to materialise it first, and
        // inventing one the caller did not supply is a worse answer than declining. Nothing
        // store-generated is expected inside an optional complex type in the first place.
        if (path.Any(p => p.IsNullable))
        {
            return null;
        }

        var entity = Expression.Parameter(typeof(object), "entity");
        var value = Expression.Parameter(typeof(object), "value");
        var typed = Expression.Convert(entity, Owner(property, path));
        var owner = Walk(typed, path);

        var target = writableField
            ? Expression.Field(owner, property.FieldInfo!)
            : Expression.Property(owner, property.PropertyInfo!);

        var assign = Expression.Assign(target, Expression.Convert(value, target.Type));

        return Expression.Lambda<Action<object, object?>>(assign, entity, value).Compile();
    }

    /// <summary>
    ///     The chain of complex properties leading to <paramref name="property" />, outermost first.
    /// </summary>
    /// <remarks>
    ///     Empty for an ordinary property. A property declared on a complex type is reached through
    ///     the members that hold it — <c>entity.Money.Audit.By</c> — and the model records that
    ///     chain in reverse, each complex type knowing the complex property that owns it.
    /// </remarks>
    private static List<IComplexProperty> ComplexPath(IProperty property)
    {
        if (property.DeclaringType is not IComplexType)
        {
            return [];
        }

        var path = new List<IComplexProperty>();
        var declaring = property.DeclaringType;

        while (declaring is IComplexType complexType)
        {
            path.Add(complexType.ComplexProperty);
            declaring = complexType.ComplexProperty.DeclaringType;
        }

        path.Reverse();

        return path;
    }

    /// <summary>The CLR type an accessor for <paramref name="property" /> casts its argument to.</summary>
    private static Type Owner(IProperty property, List<IComplexProperty> path)
        => path.Count == 0
            ? property.DeclaringType.ClrType
            : path[0].DeclaringType.ClrType;

    /// <summary>Walks <paramref name="target" /> down to the object that declares the property.</summary>
    private static Expression Walk(Expression target, List<IComplexProperty> path)
    {
        var current = target;

        foreach (var step in path)
        {
            current = step.PropertyInfo is not null
                ? Expression.Property(current, step.PropertyInfo)
                : Expression.Field(current, step.FieldInfo!);
        }

        return current;
    }

    /// <summary>
    ///     A test that every optional complex value on the path is present, or <see langword="null" />
    ///     when none of them is optional.
    /// </summary>
    private static Expression? Guard(Expression target, List<IComplexProperty> path)
    {
        Expression? guard = null;
        var current = target;

        foreach (var step in path)
        {
            current = step.PropertyInfo is not null
                ? Expression.Property(current, step.PropertyInfo)
                : Expression.Field(current, step.FieldInfo!);

            if (!step.IsNullable || current.Type.IsValueType)
            {
                continue;
            }

            var test = Expression.NotEqual(current, Expression.Constant(null, current.Type));
            guard = guard is null ? test : Expression.AndAlso(guard, test);
        }

        return guard;
    }

    private static MemberExpression Access(Expression target, IProperty property)
        => property.PropertyInfo is not null
            ? Expression.Property(target, property.PropertyInfo)
            : Expression.Field(target, property.FieldInfo!);
}
