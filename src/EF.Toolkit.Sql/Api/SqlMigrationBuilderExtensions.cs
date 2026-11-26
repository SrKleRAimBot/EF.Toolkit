using EFToolkit.Sql;
using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Dialects;
using EFToolkit.Sql.Security;

// Deliberately in EF's own namespace so these are visible in every migration file without a using.
namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>Views, routines and row-level security in migrations.</summary>
/// <remarks>
///     <para>
///         Each call renders SQL for <see cref="MigrationBuilder.ActiveProvider" /> and adds it as one
///         <c>migrationBuilder.Sql(...)</c> operation per statement, so it shows up in
///         <c>dotnet ef migrations script</c> like any other operation and runs in the migration's
///         transaction where the engine has transactional DDL.
///     </para>
///     <para>
///         A migration is generated once and replayed against whatever provider runs it, so a migration
///         that must support more than one engine renders correctly for each — but the SQL inside a view
///         or routine body is still the author's, and must be valid on every engine that runs it.
///     </para>
/// </remarks>
public static class SqlMigrationBuilderExtensions
{
    /// <summary>Creates or replaces a view.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="view">The view.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static MigrationBuilder CreateView(this MigrationBuilder migrationBuilder, ViewDefinition view)
        => Create(migrationBuilder, view);

    /// <summary>Creates or replaces a view.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="name">The view name.</param>
    /// <param name="sql">The view's query.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    ///     <code>
    ///     migrationBuilder.CreateView("ActiveCustomers",
    ///         """SELECT "Id", "Name" FROM "Customers" WHERE "IsActive" """);
    ///     </code>
    /// </example>
    public static MigrationBuilder CreateView(
        this MigrationBuilder migrationBuilder,
        string name,
        string sql,
        string? schema = null)
        => Create(migrationBuilder, new ViewDefinition(name, sql, schema));

    /// <summary>Drops a view if it exists.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="name">The view name.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static MigrationBuilder DropView(this MigrationBuilder migrationBuilder, string name, string? schema = null)
        => Add(migrationBuilder, d => ViewDefinition.DropSql(d, name, schema));

    /// <summary>Creates a PostgreSQL materialized view, replacing one of the same name.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="view">The materialized view.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not PostgreSQL.</exception>
    public static MigrationBuilder CreateMaterializedView(
        this MigrationBuilder migrationBuilder,
        MaterializedViewDefinition view)
        => Create(migrationBuilder, view);

    /// <summary>Creates a PostgreSQL materialized view, replacing one of the same name.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="name">The materialized view name.</param>
    /// <param name="sql">The view's query.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="withNoData">Create it unpopulated, leaving the first refresh to fill it.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not PostgreSQL.</exception>
    public static MigrationBuilder CreateMaterializedView(
        this MigrationBuilder migrationBuilder,
        string name,
        string sql,
        string? schema = null,
        bool withNoData = false)
        => Create(migrationBuilder, new MaterializedViewDefinition(name, sql, schema, withNoData));

    /// <summary>Drops a PostgreSQL materialized view if it exists.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="name">The materialized view name.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not PostgreSQL.</exception>
    public static MigrationBuilder DropMaterializedView(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null)
        => Add(migrationBuilder, d => MaterializedViewDefinition.DropSql(d, name, schema));

    /// <summary>Creates or replaces a stored procedure.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="procedure">The procedure.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is SQLite.</exception>
    public static MigrationBuilder CreateStoredProcedure(
        this MigrationBuilder migrationBuilder,
        StoredProcedureDefinition procedure)
        => Create(migrationBuilder, procedure);

    /// <summary>Drops a stored procedure if it exists.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="name">The procedure name.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is SQLite.</exception>
    public static MigrationBuilder DropStoredProcedure(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null)
        => Add(migrationBuilder, d => StoredProcedureDefinition.DropSql(d, name, schema));

    /// <summary>Creates or replaces a user-defined function.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="function">The function.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">
    ///     The provider is SQLite, or MySQL for a table-valued function.
    /// </exception>
    public static MigrationBuilder CreateFunction(
        this MigrationBuilder migrationBuilder,
        FunctionDefinition function)
        => Create(migrationBuilder, function);

    /// <summary>Drops a user-defined function if it exists.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="name">The function name.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is SQLite.</exception>
    public static MigrationBuilder DropFunction(
        this MigrationBuilder migrationBuilder,
        string name,
        string? schema = null)
        => Add(migrationBuilder, d => FunctionDefinition.DropSql(d, name, schema));

    /// <summary>Enables PostgreSQL row-level security on a table.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="force">
    ///     Apply the policies to the table's owner as well — usually the role the application connects
    ///     as. See <see cref="RowLevelSecurityTable" />.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not PostgreSQL.</exception>
    public static MigrationBuilder EnableRowLevelSecurity(
        this MigrationBuilder migrationBuilder,
        string table,
        string? schema = null,
        bool force = false)
        => Create(migrationBuilder, new RowLevelSecurityTable(table, schema, force));

    /// <summary>Disables PostgreSQL row-level security on a table, clearing <c>FORCE</c> too.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not PostgreSQL.</exception>
    public static MigrationBuilder DisableRowLevelSecurity(
        this MigrationBuilder migrationBuilder,
        string table,
        string? schema = null)
        => Add(migrationBuilder, d => RowLevelSecurityTable.DropSql(d, table, schema));

    /// <summary>Creates a PostgreSQL row-level security policy, replacing one of the same name.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="policy">The policy.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not PostgreSQL.</exception>
    public static MigrationBuilder CreatePolicy(this MigrationBuilder migrationBuilder, RowLevelSecurityPolicy policy)
        => Create(migrationBuilder, policy);

    /// <summary>Creates a SQL Server security policy and its predicate function, replacing both.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="policy">The policy.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not SQL Server.</exception>
    public static MigrationBuilder CreatePolicy(this MigrationBuilder migrationBuilder, SqlServerSecurityPolicy policy)
        => Create(migrationBuilder, policy);

    /// <summary>Drops a PostgreSQL row-level security policy if it exists.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="name">The policy name.</param>
    /// <param name="table">The table the policy is on.</param>
    /// <param name="schema">The table's schema, or <see langword="null" /> for the default schema.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not PostgreSQL.</exception>
    public static MigrationBuilder DropPolicy(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null)
        => Add(migrationBuilder, d => RowLevelSecurityPolicy.DropSql(d, name, table, schema));

    /// <summary>Drops a PostgreSQL row-level security policy if it exists.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="policy">The policy.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not PostgreSQL.</exception>
    public static MigrationBuilder DropPolicy(this MigrationBuilder migrationBuilder, RowLevelSecurityPolicy policy)
        => Drop(migrationBuilder, policy);

    /// <summary>Drops a SQL Server security policy and then its predicate function, if they exist.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="policy">The policy.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not SQL Server.</exception>
    public static MigrationBuilder DropPolicy(this MigrationBuilder migrationBuilder, SqlServerSecurityPolicy policy)
        => Drop(migrationBuilder, policy);

    /// <summary>Restricts a table to the current tenant's rows. See <see cref="TenantIsolationPolicy" />.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="policy">The tenant isolation to set up.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is MySQL or SQLite.</exception>
    public static MigrationBuilder CreateTenantIsolation(
        this MigrationBuilder migrationBuilder,
        TenantIsolationPolicy policy)
        => Create(migrationBuilder, policy);

    /// <summary>Removes tenant isolation from a table.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="policy">The tenant isolation that was set up.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is MySQL or SQLite.</exception>
    public static MigrationBuilder DropTenantIsolation(
        this MigrationBuilder migrationBuilder,
        TenantIsolationPolicy policy)
        => Drop(migrationBuilder, policy);

    /// <summary>Creates any database object definition.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="databaseObject">The object.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static MigrationBuilder CreateDatabaseObject(
        this MigrationBuilder migrationBuilder,
        IDatabaseObject databaseObject)
        => Create(migrationBuilder, databaseObject);

    /// <summary>Drops any database object definition.</summary>
    /// <param name="migrationBuilder">The migration being built.</param>
    /// <param name="databaseObject">The object.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static MigrationBuilder DropDatabaseObject(
        this MigrationBuilder migrationBuilder,
        IDatabaseObject databaseObject)
        => Drop(migrationBuilder, databaseObject);

    private static MigrationBuilder Create(MigrationBuilder migrationBuilder, IDatabaseObject databaseObject)
    {
        ArgumentNullException.ThrowIfNull(databaseObject);
        return Add(migrationBuilder, databaseObject.GetCreateSql);
    }

    private static MigrationBuilder Drop(MigrationBuilder migrationBuilder, IDatabaseObject databaseObject)
    {
        ArgumentNullException.ThrowIfNull(databaseObject);
        return Add(migrationBuilder, databaseObject.GetDropSql);
    }

    private static MigrationBuilder Add(
        MigrationBuilder migrationBuilder,
        Func<SqlDialect, IReadOnlyList<string>> render)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // Rendered in full before anything is added, so a refusal leaves the migration untouched
        // rather than holding the first half of a multi-statement object.
        var dialect = SqlDialectDetector.FromProviderName(migrationBuilder.ActiveProvider);
        foreach (var statement in render(dialect))
        {
            migrationBuilder.Sql(dialect == SqlDialect.SqlServer ? SqlServerBatch(statement) : statement);
        }

        return migrationBuilder;
    }

    // CREATE VIEW, PROCEDURE, FUNCTION and their OR ALTER forms must be the first statement of a batch.
    // A migration applied directly gets a batch per operation, but `dotnet ef migrations script
    // --idempotent` wraps each one in IF NOT EXISTS (...) BEGIN ... END, where they are not. EXEC of
    // the statement as a string gives it a batch of its own wherever it lands — the wrapping EF's own
    // documentation recommends for raw SQL in migrations.
    internal static string SqlServerBatch(string statement)
        => "EXEC(N'" + statement.Replace("'", "''", StringComparison.Ordinal) + "')";
}
