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
///         owning role the application usually connects as is not exempt) and creates a policy for all
///         commands comparing the tenant column with <c>current_setting('&lt;key&gt;', true)</c>, cast
///         to the column's type. On SQL Server it creates a schema-bound predicate function comparing
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
    public TenantIsolationPolicy(
        string table,
        string tenantColumn,
        string sessionKey,
        string columnStoreType,
        string? schema = null,
        string? policyName = null)
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

    /// <inheritdoc />
    public IReadOnlyList<string> GetCreateSql(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.PostgreSql =>
            [
                .. new RowLevelSecurityTable(Table, Schema, force: true).GetCreateSql(dialect),
                .. PostgreSqlPolicy().GetCreateSql(dialect),
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
                .. PostgreSqlPolicy().GetDropSql(dialect),
                .. new RowLevelSecurityTable(Table, Schema, force: true).GetDropSql(dialect),
            ],
            SqlDialect.SqlServer => SqlServerPolicy().GetDropSql(dialect),
            _ => throw NotSupported(dialect),
        };

    private RowLevelSecurityPolicy PostgreSqlPolicy()
    {
        SessionContextSql.ValidateKey(SqlDialect.PostgreSql, SessionKey);

        // NULLIF because a setting that was set and then cleared reads back as '' rather than NULL,
        // and casting '' to integer or uuid is an error on every query against the table.
        var tenant = $"CAST(NULLIF(current_setting({SqlSyntax.Literal(SqlDialect.PostgreSql, SessionKey)}, true), '') AS {ColumnStoreType})";
        var condition = $"{SqlSyntax.Quote(SqlDialect.PostgreSql, TenantColumn)} = {tenant}";

        return new RowLevelSecurityPolicy(PolicyName, Table, condition, condition, Schema);
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
