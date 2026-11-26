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
public sealed class TenantIsolationPolicy : IDatabaseObject
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
                .. (GrantsAccess ? PostgreSqlAccessPolicy().GetCreateSql(dialect) : []),
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
                .. (GrantsAccess ? PostgreSqlAccessPolicy().GetDropSql(dialect) : []),
                .. PostgreSqlPolicy().GetDropSql(dialect),
                DisableIfUnpoliced(),
            ],
            SqlDialect.SqlServer => SqlServerPolicy().GetDropSql(dialect),
            _ => throw NotSupported(dialect),
        };

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

        // NULLIF because a setting that was set and then cleared reads back as '' rather than NULL,
        // and casting '' to integer or uuid is an error on every query against the table.
        var tenant = $"CAST(NULLIF(current_setting({SqlSyntax.Literal(SqlDialect.PostgreSql, SessionKey)}, true), '') AS {ColumnStoreType})";
        var condition = $"{SqlSyntax.Quote(SqlDialect.PostgreSql, TenantColumn)} = {tenant}";

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
            [new SqlServerPredicateParameter("tenant", ColumnStoreType, TenantColumn)],
            $"@tenant = CAST(SESSION_CONTEXT({SqlSyntax.Literal(SqlDialect.SqlServer, SessionKey)}) AS {ColumnStoreType})",
            Schema,
            block: SqlServerBlockOperations.AfterInsert | SqlServerBlockOperations.AfterUpdate);
    }

    private static SqlFeatureNotSupportedException NotSupported(SqlDialect dialect)
        => SqlFeatureNotSupportedException.For(
            Feature,
            dialect,
            $"{SqlSyntax.EngineName(dialect)} has no row-level security; isolate tenants in the "
            + "application instead, for example with a global query filter on the tenant column.");
}
