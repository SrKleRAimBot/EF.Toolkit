using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Security;
using EFToolkit.Sql.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Sql.Tests;

/// <summary>Database objects against a real SQLite database, kept alive by holding its connection open.</summary>
public sealed class SqliteRuntimeTests : IAsyncLifetime
{
    private static readonly ViewDefinition ActiveCustomers = new(
        "ActiveCustomers",
        """SELECT "Id", "Name" FROM "Customers" WHERE "IsActive" = 1;""");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ShopContext _context = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        _context = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        _context.Customers.AddRange(
            new Customer { Name = "Ada", IsActive = true },
            new Customer { Name = "Brendan", IsActive = false },
            new Customer { Name = "Cleo", IsActive = true });
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task EnsureCreated_does_not_create_the_view_itself()
    {
        // The reason CreateDatabaseObjectsAsync exists: a keyless entity mapped ToView is assumed to
        // exist already, so a test database built with EnsureCreated has nothing behind it.
        await Should.ThrowAsync<SqliteException>(() => _context.ActiveCustomers.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_created_view_is_queryable_through_a_keyless_entity()
    {
        await _context.Database.CreateDatabaseObjectsAsync(ActiveCustomers);

        var names = await _context.ActiveCustomers.OrderBy(c => c.Name).Select(c => c.Name).ToListAsync(TestContext.Current.CancellationToken);

        names.ShouldBe(["Ada", "Cleo"]);
    }

    [Fact]
    public async Task A_set_that_fails_part_way_leaves_nothing_behind()
    {
        // One transaction for the set: the first view must not survive the second's failure.
        var broken = new ViewDefinition("Broken", "SELEC nothing");   // SQLite accepts a view over a missing table, not bad syntax

        await Should.ThrowAsync<SqliteException>(() =>
            _context.Database.CreateDatabaseObjectsAsync([ActiveCustomers, broken], TestContext.Current.CancellationToken));

        await Should.ThrowAsync<SqliteException>(() => _context.ActiveCustomers.ToListAsync(TestContext.Current.CancellationToken));
        _context.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task Inside_a_callers_transaction_a_failed_replacement_cannot_be_committed_half_done()
    {
        // SQLite keeps the transaction committable after a failed statement: without a savepoint, a
        // caller that caught the error and committed kept the DROP VIEW and lost the view.
        await _context.Database.CreateDatabaseObjectsAsync(ActiveCustomers);
        var replacement = new ViewDefinition("ActiveCustomers", "SELEC nothing");

        await using (var transaction = await _context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await Should.ThrowAsync<SqliteException>(() => _context.Database.CreateDatabaseObjectsAsync(replacement));
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await _context.ActiveCustomers.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    private sealed class CancelAfterCreate(CancellationTokenSource source) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CREATE VIEW", StringComparison.Ordinal))
            {
                source.Cancel();
            }

            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Cancelling_after_the_last_statement_ran_does_not_report_a_failure_that_did_not_happen()
    {
        // The last statement had run; releasing the savepoint with the cancelled token threw, so the
        // caller was told the call failed while the view stood in their transaction to be committed.
        using var source = new CancellationTokenSource();
        await using var context = new ShopContext(new DbContextOptionsBuilder<ShopContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new CancelAfterCreate(source))
            .Options);

        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await context.Database.CreateDatabaseObjectsAsync([ActiveCustomers], source.Token);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        source.IsCancellationRequested.ShouldBeTrue();
        (await _context.ActiveCustomers.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task A_callers_transaction_is_joined_rather_than_nested()
    {
        await using (var transaction = await _context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await _context.Database.CreateDatabaseObjectsAsync(ActiveCustomers);
            (await _context.ActiveCustomers.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await Should.ThrowAsync<SqliteException>(() => _context.ActiveCustomers.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Creating_again_replaces_the_view()
    {
        await _context.Database.CreateDatabaseObjectsAsync(ActiveCustomers);
        await _context.Database.CreateDatabaseObjectsAsync(
            new ViewDefinition("ActiveCustomers", """SELECT "Id", "Name" FROM "Customers" WHERE "IsActive" = 0"""));

        (await _context.ActiveCustomers.Select(c => c.Name).ToListAsync(TestContext.Current.CancellationToken)).ShouldBe(["Brendan"]);
    }

    [Fact]
    public async Task Dropping_removes_the_view_and_is_safe_to_repeat()
    {
        await _context.Database.CreateDatabaseObjectsAsync([ActiveCustomers], TestContext.Current.CancellationToken);
        await _context.Database.DropDatabaseObjectsAsync(ActiveCustomers);
        await _context.Database.DropDatabaseObjectsAsync([ActiveCustomers], TestContext.Current.CancellationToken);

        await Should.ThrowAsync<SqliteException>(() => _context.ActiveCustomers.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dependent_views_are_created_in_order_and_dropped_in_reverse()
    {
        var dependent = new ViewDefinition("ActiveNames", """SELECT "Name" FROM "ActiveCustomers" """);

        await _context.Database.CreateDatabaseObjectsAsync(ActiveCustomers, dependent);
        (await _context.Database.SqlQueryRaw<string>("""SELECT "Name" AS "Value" FROM "ActiveNames" ORDER BY 1""")
            .ToListAsync(TestContext.Current.CancellationToken)).ShouldBe(["Ada", "Cleo"]);

        await _context.Database.DropDatabaseObjectsAsync(ActiveCustomers, dependent);

        (await _context.Database.SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM sqlite_master WHERE type = 'view'""")
            .SingleAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Braces_in_a_view_body_reach_the_database_intact()
    {
        await _context.Database.CreateDatabaseObjectsAsync(new ViewDefinition("Json", """SELECT '{"a":1}' AS "Value" """));

        (await _context.Database.SqlQueryRaw<string>("""SELECT "Value" FROM "Json" """).SingleAsync(TestContext.Current.CancellationToken))
            .ShouldBe("""{"a":1}""");
    }

    [Fact]
    public async Task A_refused_object_executes_nothing_at_all()
    {
        var ex = await Should.ThrowAsync<SqlFeatureNotSupportedException>(() =>
            _context.Database.CreateDatabaseObjectsAsync(ActiveCustomers, new StoredProcedureDefinition("p", null, "x")));
        ex.Dialect.ShouldBe(SqlDialect.Sqlite);

        await Should.ThrowAsync<SqliteException>(() => _context.ActiveCustomers.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Null_objects_are_refused()
    {
        await Should.ThrowAsync<ArgumentException>(() => _context.Database.CreateDatabaseObjectsAsync(ActiveCustomers, null!));
        await Should.ThrowAsync<ArgumentException>(() => _context.Database.DropDatabaseObjectsAsync(ActiveCustomers, null!));
        await Should.ThrowAsync<ArgumentNullException>(() => _context.Database.CreateDatabaseObjectsAsync((IEnumerable<IDatabaseObject>)null!));
    }

    [Fact]
    public async Task Features_SQLite_lacks_are_refused_at_runtime()
    {
        var db = _context.Database;
        var ct = TestContext.Current.CancellationToken;

        (await Should.ThrowAsync<SqlFeatureNotSupportedException>(() => db.ExecuteProcedureAsync("p", new { a = 1 }, cancellationToken: ct)))
            .Feature.ShouldBe("Calling stored procedures");
        await Should.ThrowAsync<SqlFeatureNotSupportedException>(() => db.QueryProcedureAsync<int>("p", cancellationToken: ct));
        Should.Throw<SqlFeatureNotSupportedException>(() => db.QueryFunction<int>("f"));
        (await Should.ThrowAsync<SqlFeatureNotSupportedException>(() => db.SetSessionContextAsync("k", 1, ct)))
            .Feature.ShouldBe("Session context");
        await Should.ThrowAsync<SqlFeatureNotSupportedException>(() => db.RefreshMaterializedViewAsync("m", cancellationToken: ct));
        await Should.ThrowAsync<SqlFeatureNotSupportedException>(() =>
            db.CreateDatabaseObjectsAsync(new TenantIsolationPolicy("Customers", "Id", "app.t", "integer")));
    }

    [Fact]
    public void UseSessionContext_on_sqlite_fails_as_soon_as_the_context_is_constructed()
    {
        // EF validates options extensions when it builds the context's services, so this surfaces
        // at construction rather than on whichever query first opens a connection.
        var options = new DbContextOptionsBuilder<ShopContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSessionContext(s => s.Set("tenant", () => 1))
            .Options;

        Should.Throw<SqlFeatureNotSupportedException>(() => new ShopContext(options)).Dialect.ShouldBe(SqlDialect.Sqlite);
    }

    [Fact]
    public async Task The_interceptor_does_nothing_for_a_context_without_session_context()
    {
        // Registered directly, so the only thing between it and a query is its own "nothing
        // configured" check.
        await using var context = new ShopContext(new DbContextOptionsBuilder<ShopContext>()
            .UseSqlite("Data Source=:memory:")
            .AddInterceptors(SessionContextInterceptor.Instance)
            .Options);

        context.Database.OpenConnection();
        context.Database.CloseConnection();
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        (await context.Customers.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    private sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
    {
        public DbSet<Customer> Customers => Set<Customer>();

        public DbSet<ActiveCustomer> ActiveCustomers => Set<ActiveCustomer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<ActiveCustomer>().HasNoKey().ToView("ActiveCustomers");
    }

    private sealed class Customer
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public bool IsActive { get; set; }
    }

    private sealed class ActiveCustomer
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }
}
