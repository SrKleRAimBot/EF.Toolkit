using System.Text.RegularExpressions;
using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Dialects;
using EFToolkit.Sql.Sessions;

namespace EFToolkit.Sql.Security;

/// <summary>
///     Restricts a table to the rows of the tenant named in the session context — the whole
///     engine-appropriate row-level security setup in one definition.
/// </summary>
/// <remarks>
///     <para>
///         On PostgreSQL this enables and <em>forces</em> row-level security on the table (so the
///         owning role the application usually connects as is not exempt) and creates a
///         <em>restrictive</em> policy for all commands comparing the tenant column with
///         <c>current_setting('&lt;key&gt;', true)</c>, cast to the column's type, over a permissive
///         policy (<see cref="AccessPolicyName" />) granting every row. Restrictive, so that no other
///         permissive policy on the table can widen a session past its tenant. Dropping it switches
///         row-level security off only if no other policy is left on the table. On SQL Server it creates a schema-bound predicate function comparing
///         the column with <c>SESSION_CONTEXT(N'&lt;key&gt;')</c> and a security policy that filters
///         reads and blocks inserts and updates into another tenant.
///     </para>
///     <para>
///         Both fail closed. With no tenant in the session context — or, on PostgreSQL, an empty one —
///         the comparison is <c>NULL</c> and no row matches: a request that forgot to set its tenant sees
///         nothing and can write nothing, rather than seeing everything.
///     </para>
///     <para>
///         Set the tenant with <c>UseSessionContext</c> on the context options, which applies it on every
///         connection open, or once with <c>Database.SetSessionContextAsync</c>. PostgreSQL superusers
///         and roles with <c>BYPASSRLS</c> are never subject to row-level security, so connect as an
///         ordinary role. MySQL and SQLite have no row-level security, and throw.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     migrationBuilder.CreateTenantIsolation(
///         new TenantIsolationPolicy("Orders", "TenantId", "app.tenant_id", "integer"));
///     </code>
/// </example>
public sealed partial class TenantIsolationPolicy : IDatabaseObject
{
    internal const string Feature = "Tenant isolation (row-level security)";

    /// <summary>Initializes a new instance of the <see cref="TenantIsolationPolicy" /> class.</summary>
    /// <param name="table">The table to isolate.</param>
    /// <param name="tenantColumn">The column holding each row's tenant.</param>
    /// <param name="sessionKey">
    ///     The session-context key holding the current tenant. On PostgreSQL this is a custom setting and
    ///     must be dotted, such as <c>app.tenant_id</c>.
    /// </param>
    /// <param name="columnStoreType">The tenant column's database type, which the session value is cast to.</param>
    /// <param name="schema">The table's schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="policyName">
    ///     The policy name. <see langword="null" /> for <c>&lt;table&gt;_tenant_isolation</c>; the SQL
    ///     Server predicate function is named after the policy with a <c>_predicate</c> suffix.
    /// </param>
    /// <param name="grantAccess">
    ///     PostgreSQL only. <see langword="true" /> (the default) also creates the permissive
    ///     <see cref="AccessPolicyName" /> policy granting every row, which the restrictive tenant policy
    ///     then narrows — without some permissive policy PostgreSQL grants nothing. Pass
    ///     <see langword="false" /> when the table's own permissive policies already decide which rows a
    ///     role may see: a blanket grant would widen them to every row of the tenant.
    /// </param>
    public TenantIsolationPolicy(
        string table,
        string tenantColumn,
        string sessionKey,
        string columnStoreType,
        string? schema = null,
        string? policyName = null,
        bool grantAccess = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnStoreType);

        // The type is read to decide which comparisons make the cast lossless; a comment inside it
        // ("pg_catalog /* … */ . interval") would hide what the database will actually cast to.
        if (columnStoreType.Contains("--", StringComparison.Ordinal) || columnStoreType.Contains("/*", StringComparison.Ordinal))
        {
            throw new ArgumentException("The column store type cannot contain a SQL comment.", nameof(columnStoreType));
        }

        if (policyName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        }

        Table = table;
        TenantColumn = tenantColumn;
        SessionKey = sessionKey;
        ColumnStoreType = columnStoreType.Trim();
        Schema = SqlSyntax.OptionalName(schema, nameof(schema));
        PolicyName = policyName ?? table + "_tenant_isolation";
        GrantsAccess = grantAccess;
    }

    /// <summary>The isolated table.</summary>
    public string Table { get; }

    /// <summary>The column holding each row's tenant.</summary>
    public string TenantColumn { get; }

    /// <summary>The session-context key holding the current tenant.</summary>
    public string SessionKey { get; }

    /// <summary>The tenant column's database type.</summary>
    public string ColumnStoreType { get; }

    /// <summary>The table's schema, or <see langword="null" /> for the default schema.</summary>
    public string? Schema { get; }

    /// <summary>The policy name.</summary>
    public string PolicyName { get; }

    /// <summary>Whether the PostgreSQL rendering includes the permissive base grant.</summary>
    public bool GrantsAccess { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCreateSql(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.PostgreSql =>
            [
                .. new RowLevelSecurityTable(Table, Schema, force: true).GetCreateSql(dialect),
                .. PostgreSqlPolicy().GetCreateSql(dialect),
                .. (GrantsAccess ? PostgreSqlAccessPolicy().GetCreateSql(dialect) : WithdrawnAccess()),
            ],
            SqlDialect.SqlServer => SqlServerPolicy().GetCreateSql(dialect),
            _ => throw NotSupported(dialect),
        };

    /// <inheritdoc />
    public IReadOnlyList<string> GetDropSql(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.PostgreSql =>
            [
                // The "_access" grant goes whichever grantAccess this definition says: one dropped with
                // grantAccess false after an install with it would otherwise leave the grant behind,
                // granting every tenant's rows once the restrictive policy is gone.
                .. (GrantsAccess ? PostgreSqlAccessPolicy().GetDropSql(dialect) : WithdrawnAccess()),
                .. PostgreSqlPolicy().GetDropSql(dialect),
                DisableIfUnpoliced(),
            ],
            SqlDialect.SqlServer => SqlServerPolicy().GetDropSql(dialect),
            _ => throw NotSupported(dialect),
        };

    // Without the base grant, an earlier install with it may have left the "_access" policy behind, which
    // would go on granting every row of the tenant over the table's own permissive policies — so it is
    // removed. Only when its name fits: a longer one would be truncated to some other name (the tenant
    // policy's own, for a 63-byte policyName), and an install with the grant refuses such a name anyway,
    // so there is nothing of that name to remove.
    private IReadOnlyList<string> WithdrawnAccess()
        => System.Text.Encoding.UTF8.GetByteCount(AccessPolicyName) <= PostgreSqlIdentifierBytes
            ? PostgreSqlAccessPolicy().GetDropSql(SqlDialect.PostgreSql)
            : [];

    // PostgreSQL truncates identifiers past 63 bytes without an error. Two names that agree on their
    // first 63 bytes are then one policy — and since each create is preceded by DROP POLICY IF EXISTS,
    // creating the access policy would silently drop the tenant policy just created, leaving USING (true).
    private const int PostgreSqlIdentifierBytes = 63;

    private void RequireDistinctPostgreSqlNames()
    {
        var longest = GrantsAccess ? AccessPolicyName : PolicyName;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(longest);
        if (bytes > PostgreSqlIdentifierBytes)
        {
            throw new ArgumentException(
                $"The PostgreSQL policy name '{longest}' is {bytes} bytes; PostgreSQL truncates names past "
                + $"{PostgreSqlIdentifierBytes}, which can merge the tenant policy with its '_access' "
                + "counterpart and silently remove tenant isolation. Pass a shorter policyName.",
                "policyName");
        }
    }

    private RowLevelSecurityPolicy PostgreSqlPolicy()
    {
        SessionContextSql.ValidateKey(SqlDialect.PostgreSql, SessionKey);
        RequireDistinctPostgreSqlNames();

        // Only built-in types this policy knows how to cast losslessly are taken as keys. Each other
        // built-in loses something on the way from text in its own way — an interval rounds '0.4
        // microseconds' away, a real 16777217 to 16777216, a daterange a bound's time of day — and an
        // array casts every element so: no check on the text keeps up with all of them. A name that is
        // no built-in is a type of the developer's own, cast by its own rules (see the docs).
        var typeName = PostgreSqlTypeName(ColumnStoreType, out var isArray);
        // An interval may also carry its fields — interval day to second — which no set of names lists.
        // pg_catalog also names each built-in array by its element with an underscore: _float4 is real[].
        if (isArray
            || (typeName.StartsWith('_') && PostgreSqlBuiltInTypes.Contains(typeName[1..]))
            || IntervalWithFields().IsMatch(typeName)
            || (PostgreSqlBuiltInTypes.Contains(typeName) && !PostgreSqlKeyTypes.Contains(typeName)))
        {
            throw new ArgumentException(
                $"'{ColumnStoreType}' cannot be a PostgreSQL tenant key: a cast to it can lose data, so a session "
                + "value could match a tenant it does not name. Use an integer, numeric, text, uuid, date or "
                + "timestamp key.",
                "columnStoreType");
        }

        // NULLIF because a setting that was set and then cleared reads back as '' rather than NULL,
        // and casting '' to integer or uuid is an error on every query against the table.
        var setting = $"NULLIF(current_setting({SqlSyntax.Literal(SqlDialect.PostgreSql, SessionKey)}, true), '')";
        var tenant = $"CAST({setting} AS {ColumnStoreType})";

        // The cast must also be lossless. A type modifier is where PostgreSQL loses data on a cast:
        // varchar(8) truncates 'acme-dev2' to 'acme-dev', numeric(10,2) rounds 12.555 to 12.56,
        // timestamp(0) rounds away fractions — and a session carrying the longer value would match the
        // shorter tenant's rows. So the constrained cast must equal the same value cast without the
        // modifier. This compares values, not renderings: 12.5 is 12.50 and an ISO 8601 timestamp is
        // the timestamp PostgreSQL prints with a space, while a truncation or rounding is still refused.
        // A type without a modifier loses nothing on the cast, so there is nothing to compare. The
        // column side of the first comparison stays index-friendly either way.
        var condition = $"{SqlSyntax.Quote(SqlDialect.PostgreSql, TenantColumn)} = {tenant}";
        var unconstrained = PostgreSqlUnconstrained(ColumnStoreType);
        if (unconstrained is not null)
        {
            condition += $" AND {tenant} = CAST({setting} AS {unconstrained})";
        }

        // PostgreSQL's date and time input takes far more than any key holds: compact times (T123456),
        // single-digit fields, zone names, digits finer than the microsecond it rounds them to, and clock
        // fields past their range that it carries over (12:34:60 is 12:35:00, 24:00 the next midnight).
        // A type without a time zone also drops whatever zone a setting carries, and a date the time of
        // day. Any of that could land a session value on another tenant's key. So a temporal setting
        // must be in one plain ISO 8601 form, which is what UseSessionContext writes:
        //   date         2026-11-27, or that day at an all-zero midnight           (DateOnly)
        //   timestamp    2026-11-27T12:30:00.1234560, a space for the T allowed    (unspecified DateTime)
        //   time         12:30:00.1234560                                          (TimeOnly)
        //   timestamptz  a timestamp ending in Z or an offset such as +02:00       (UTC DateTime, DateTimeOffset)
        //   timetz       a time ending in Z or an offset
        // Seconds and fraction are optional, a fraction keeps at most six significant digits, and every
        // field is inside its range.
        if (TemporalFormats.TryGetValue(typeName, out var format))
        {
            condition += $" AND {setting} ~ {format}";
        }

        // Restrictive: PostgreSQL ORs permissive policies together, so a permissive tenant check
        // would be bypassed by any other permissive policy on the table — a reporting policy with
        // USING (true) would read across tenants. Restrictive policies are ANDed onto the result.
        return new RowLevelSecurityPolicy(
            PolicyName, Table, condition, condition, Schema, kind: PolicyKind.Restrictive);
    }

    /// <summary>The name of the permissive policy that grants access the tenant policy then narrows.</summary>
    public string AccessPolicyName => PolicyName + "_access";

    // A restrictive policy only ever removes rows: with no permissive policy at all, PostgreSQL grants
    // nothing. This one grants every row, for the restrictive tenant policy to narrow. Any other
    // permissive policy on the table is subsumed by it — which loses nothing, because permissive
    // policies only ever add access — while restrictive ones still apply on top.
    private RowLevelSecurityPolicy PostgreSqlAccessPolicy()
        => new(AccessPolicyName, Table, "true", "true", Schema);

    // Row-level security is switched off again only if nothing else depends on it. Disabling it
    // unconditionally would leave any other policy on the table defined but no longer enforced.
    private string DisableIfUnpoliced()
    {
        // The relation is resolved by regclass exactly as the ALTER TABLE below resolves it — through
        // search_path when unqualified — so the check and the change can never look at two different
        // tables. Comparing a schema name with current_schema() could: an unqualified name resolves to
        // the first schema on the path that has it, which need not be the first schema on the path.
        var target = SqlSyntax.Qualify(SqlDialect.PostgreSql, Table, Schema);
        var relation = SqlSyntax.Literal(SqlDialect.PostgreSql, target);

        // Quoted by DollarQuote, which picks a tag the body does not contain: a fixed tag could appear
        // inside a valid quoted table name and close the block early.
        var body = $"""
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_policy WHERE polrelid = {relation}::regclass)
                THEN
                    ALTER TABLE {target} NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE {target} DISABLE ROW LEVEL SECURITY;
                END IF;
            END
            """;

        return "DO " + SqlSyntax.DollarQuote(body);
    }

    private SqlServerSecurityPolicy SqlServerPolicy()
    {
        SessionContextSql.ValidateKey(SqlDialect.SqlServer, SessionKey);

        return new SqlServerSecurityPolicy(
            PolicyName,
            Table,
            PolicyName + "_predicate",
            [new SqlServerPredicateParameter("tenant", SqlServerParameterType(), TenantColumn)],
            SqlServerCondition(),
            Schema,
            block: SqlServerBlockOperations.AfterInsert | SqlServerBlockOperations.AfterUpdate);
    }

    // As on PostgreSQL, the conversion from the session value must be lossless: a string holding a GUID
    // followed by extra text converts to that GUID, an nvarchar(8) column truncates a longer value, and
    // decimal(10,2) rounds 12.555 — each matching a different tenant's rows. Either of two checks
    // shows nothing was lost:
    //  - the converted value, as a sql_variant, equals the session value. sql_variant compares values
    //    of one type family by value (decimal 12.50 equals 12.5, datetime2(0) equals a datetime2 with
    //    no fraction), and values of different families never as equal, so a string never passes this
    //    way for a GUID or number column;
    //  - or the session value is a string, and the converted value reads back as its very text,
    //    compared as bytes because string equality ignores trailing spaces. This is how a string passes
    //    for a column of another type: only as that value's own canonical text. A typed value never
    //    takes this way, because default text conversions are themselves lossy: a float 1.00000001
    //    and the real 1 it rounds to both print as "1".
    //
    // A character tenant is also compared byte for byte. The predicate's parameter takes the database's
    // default collation, not the column's, so under a case-insensitive default a binary-collated column
    // holding both "acme" and "ACME" would otherwise give either tenant both sets of rows.
    //
    // Bytes are compared with their lengths, because SQL Server pads the shorter of two binary values with
    // zeros. A character column takes the text route only: a string's sql_variant comparison pads trailing
    // spaces too (an nvarchar(4) column truncates "acme " to "acme", which then compares equal), a typed
    // value is of another family and never equal there anyway, and nvarchar(max) cannot be converted to
    // sql_variant at all. The session value itself is a sql_variant of at most 8000 bytes, so
    // nvarchar(4000) holds all of it.
    private string SqlServerCondition()
    {
        var value = $"SESSION_CONTEXT({SqlSyntax.Literal(SqlDialect.SqlServer, SessionKey)})";
        var tenant = $"CAST({value} AS {ColumnStoreType})";
        var asText = $"CAST({tenant} AS nvarchar(4000))";
        var valueText = $"CAST({value} AS nvarchar(4000))";
        var text = $"(SQL_VARIANT_PROPERTY({value}, 'BaseType') IN ('nvarchar', 'varchar', 'nchar', 'char') "
            + $"AND CAST({asText} AS varbinary(8000)) = CAST({valueText} AS varbinary(8000)) "
            + $"AND DATALENGTH({asText}) = DATALENGTH({valueText}))";

        // A character tenant arrives as nvarchar (see SqlServerParameterType), so it holds the row's own
        // characters, and its UTF-16 bytes are compared with the session string's directly. No cast to the
        // column's type is needed: comparing with the row's text as it is cannot be fooled by truncation.
        // Both sides whole: a varchar session value may hold 8000 characters, and cut to 4000 two keys that
        // differ only past that would compare equal. Converting a sql_variant straight to nvarchar(max) is
        // itself cut at 4000, so a varchar one goes through varchar(8000) first; an nvarchar one holds at
        // most 4000 characters (sql_variant holds 8000 bytes), which nvarchar(4000) keeps whole.
        if (IsSqlServerCharacter(ColumnStoreType))
        {
            var fullText = $"(CASE WHEN SQL_VARIANT_PROPERTY({value}, 'BaseType') IN ('varchar', 'char') "
                + $"THEN CAST(CAST({value} AS varchar(8000)) AS nvarchar(max)) ELSE CAST({value} AS nvarchar(4000)) END)";
            return $"@tenant = {fullText} AND SQL_VARIANT_PROPERTY({value}, 'BaseType') IN ('nvarchar', 'varchar', 'nchar', 'char') "
                + $"AND CAST(@tenant AS varbinary(max)) = CAST({fullText} AS varbinary(max)) "
                + $"AND DATALENGTH(@tenant) = DATALENGTH({fullText})";
        }

        return $"@tenant = {tenant} AND (CAST({tenant} AS sql_variant) = {value} OR {text})";
    }

    // A varchar parameter would take the database's default collation, and with it its code page: a UTF-8
    // column's 東京 passed into it under a Latin-1 default becomes ?? before anything is compared. nvarchar
    // holds every character any varchar column can, so a character tenant is passed in as that.
    private string SqlServerParameterType()
        => IsSqlServerCharacter(ColumnStoreType) ? "nvarchar(max)" : ColumnStoreType;

    private static bool IsSqlServerCharacter(string storeType)
    {
        var bare = TypeModifier().Replace(storeType, "");
        bare = MaxModifier().Replace(bare, "");
        var name = Whitespace().Replace(bare.Trim(), " ").ToLowerInvariant();
        if (name.StartsWith("sys.", StringComparison.Ordinal))
        {
            name = name[4..];
        }

        return name.Trim('[', ']') is "char" or "varchar" or "nchar" or "nvarchar" or "sysname" or "character"
            or "char varying" or "character varying" or "national character" or "national char"
            or "national character varying" or "national char varying";
    }

    /// <summary>
    ///     <paramref name="storeType" /> with its length, precision or scale modifiers removed, or
    ///     <see langword="null" /> when it has none.
    /// </summary>
    /// <remarks>
    ///     <c>char</c> and <c>bit</c> are the two types whose bare name is not unbounded: bare, they mean
    ///     length 1. Their unbounded forms are <c>bpchar</c> and <c>varbit</c>.
    /// </remarks>
    internal static string? PostgreSqlUnconstrained(string storeType)
    {
        var bare = TypeModifier().Replace(storeType, "");
        var modified = bare.Length != storeType.Length;

        bare = TightenDots(Whitespace().Replace(bare.Trim(), " "));
        var schema = bare.StartsWith("pg_catalog.", StringComparison.OrdinalIgnoreCase) ? bare[..11]
            : bare.StartsWith("\"pg_catalog\".", StringComparison.Ordinal) ? bare[..13]
            : "";
        var name = bare[schema.Length..];
        var array = name.EndsWith("[]", StringComparison.Ordinal) ? "[]" : "";
        name = name[..^array.Length].TrimEnd();

        // A quoted name is the type of exactly that spelling: "name" is name, but "char" is PostgreSQL's
        // one-byte internal type, not character — it keeps one byte of 'acme' too. A quoted name in
        // capitals is some other, user-defined type.
        var quoted = name.Length > 2 && name[0] == '"' && name[^1] == '"';
        name = quoted ? name[1..^1] : name.ToLowerInvariant();

        // Bare, these still have a length: character is character(1) and bit is bit(1), so 'acme' cast to
        // character is 'a', and name keeps only its first 63 bytes. They need the comparison with or
        // without a modifier.
        var unbounded = (quoted, name) switch
        {
            (true, "char") or (_, "name") => schema + "text" + array,
            (false, "char" or "character" or "nchar" or "national character" or "national char") => schema + "bpchar" + array,
            (_, "bit") => schema + "varbit" + array,
            _ => null,
        };

        return unbounded ?? (modified ? bare : null);
    }

    // The one form a temporal setting may take, every field inside its range. A fraction keeps at most
    // six significant digits: 1234560 is allowed, 1234567 is not.
    private const string IsoDate = "[0-9]{4,}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])";
    private const string IsoTime = "([01][0-9]|2[0-3]):[0-5][0-9](:[0-5][0-9]([.][0-9]{0,6}0*)?)?";
    private const string IsoOffset = "([Zz]|[+-][0-9]{2}(:?[0-5][0-9])?)";

    private static readonly Dictionary<string, string> TemporalFormats = new(StringComparer.Ordinal)
    {
        ["date"] = $"'^{IsoDate}([T ]00:00(:00([.]0*)?)?)?$'",
        ["timestamp"] = $"'^{IsoDate}[T ]{IsoTime}$'",
        ["timestamp without time zone"] = $"'^{IsoDate}[T ]{IsoTime}$'",
        ["time"] = $"'^{IsoTime}$'",
        ["time without time zone"] = $"'^{IsoTime}$'",
        ["timestamptz"] = $"'^{IsoDate}[T ]{IsoTime}{IsoOffset}$'",
        ["timestamp with time zone"] = $"'^{IsoDate}[T ]{IsoTime}{IsoOffset}$'",
        ["timetz"] = $"'^{IsoTime}{IsoOffset}$'",
        ["time with time zone"] = $"'^{IsoTime}{IsoOffset}$'",
    };

    /// <summary>
    ///     The type's own name without modifier or pg_catalog schema: lower-cased, or as written if quoted.
    /// </summary>
    private static string PostgreSqlTypeName(string storeType) => PostgreSqlTypeName(storeType, out _);

    /// <inheritdoc cref="PostgreSqlTypeName(string)" />
    /// <param name="storeType">The column's store type.</param>
    /// <param name="isArray">Whether it is an array, whose name is then its element type's.</param>
    private static string PostgreSqlTypeName(string storeType, out bool isArray)
    {
        var bare = TightenDots(Whitespace().Replace(TypeModifier().Replace(storeType, "").Trim(), " "));
        var element = ArraySuffix().Replace(bare, "");
        isArray = element.Length != bare.Length;
        bare = element.StartsWith("pg_catalog.", StringComparison.OrdinalIgnoreCase) ? element[11..]
            : element.StartsWith("\"pg_catalog\".", StringComparison.Ordinal) ? element[13..]
            : element;
        return bare.Length > 2 && bare[0] == '"' && bare[^1] == '"' ? bare[1..^1] : bare.ToLowerInvariant();
    }

    // PostgreSQL takes "pg_catalog . timestamp" for pg_catalog.timestamp: spaces around a qualifying dot
    // are not part of either name. Inside a quoted name they are, so quoted parts are kept as written.
    private static string TightenDots(string type)
        => DotOrQuoted().Replace(type, m => m.Groups[1].Success ? m.Value : ".");

    [GeneratedRegex(@"(""(?:[^""]|"""")*"")|\s*\.\s*", RegexOptions.CultureInvariant)]
    private static partial Regex DotOrQuoted();

    /// <summary>The built-in types a tenant key may have: each is cast losslessly, or checked so it is.</summary>
    private static readonly HashSet<string> PostgreSqlKeyTypes = new(StringComparer.Ordinal)
    {
        "smallint", "int2", "integer", "int", "int4", "bigint", "int8", "numeric", "decimal",
        "text", "varchar", "character varying", "char", "character", "bpchar", "nchar", "national character",
        "national char", "national character varying", "national char varying", "nchar varying", "name",
        "bit", "varbit", "bit varying", "uuid", "date",
        "timestamp", "timestamp without time zone", "timestamptz", "timestamp with time zone",
        "time", "time without time zone", "timetz", "time with time zone",
    };

    /// <summary>
    ///     Every type pg_catalog defines (PostgreSQL 17), under its own name and its SQL spellings: a name
    ///     among these and not a key type is refused; a name among neither is a type of the developer's own.
    /// </summary>
    private static readonly HashSet<string> PostgreSqlBuiltInTypes = new(StringComparer.Ordinal)
    {
        "aclitem", "bit", "bool", "boolean", "box", "bpchar", "bytea", "char", "cid", "cidr", "circle", "date",
        "datemultirange", "daterange", "float4", "float8", "real", "double precision", "float", "gtsvector",
        "inet", "int2", "int2vector", "int4", "int4multirange", "int4range", "int8", "int8multirange",
        "int8range", "interval", "json", "jsonb", "jsonpath", "line", "lseg", "macaddr", "macaddr8", "money",
        "name", "numeric", "nummultirange", "numrange", "oid", "oidvector", "path", "pg_brin_bloom_summary",
        "pg_brin_minmax_multi_summary", "pg_dependencies", "pg_lsn", "pg_mcv_list", "pg_ndistinct",
        "pg_node_tree", "pg_snapshot", "point", "polygon", "refcursor", "regclass", "regcollation",
        "regconfig", "regdictionary", "regnamespace", "regoper", "regoperator", "regproc", "regprocedure",
        "regrole", "regtype", "text", "tid", "time", "timestamp", "timestamptz", "timetz", "tsmultirange",
        "tsquery", "tsrange", "tstzmultirange", "tstzrange", "tsvector", "txid_snapshot", "uuid", "varbit",
        "varchar", "xid", "xid8", "xml",
    };

    [GeneratedRegex(@"^interval\s", RegexOptions.CultureInvariant)]
    private static partial Regex IntervalWithFields();

    // Any number of dimensions, spaced or not, sized or not — real [], real[][], date[3] — or the SQL
    // standard's ARRAY and ARRAY[3].
    [GeneratedRegex(@"((\s*\[\s*\d*\s*\])+|\s+array(\s*\[\s*\d*\s*\])?)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ArraySuffix();

    [GeneratedRegex(@"\s*\(\s*-?\d+\s*(,\s*-?\d+\s*)?\)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeModifier();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*\(\s*max\s*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MaxModifier();

    private static SqlFeatureNotSupportedException NotSupported(SqlDialect dialect)
        => SqlFeatureNotSupportedException.For(
            Feature,
            dialect,
            $"{SqlSyntax.EngineName(dialect)} has no row-level security; isolate tenants in the "
            + "application instead, for example with a global query filter on the tenant column.");
}
