using System.Data.Common;
using System.Globalization;
using EFToolkit.Testing.Integration.Infrastructure;
using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Testing.Integration;

/// <summary>Every feature that sends engine-specific SQL, against the engine itself.</summary>
public abstract class EngineTests(EngineFixture fixture) : IAsyncLifetime
{
    protected EngineFixture Fixture { get; } = fixture;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (Fixture.SkipReason is null)
        {
            await Fixture.Database.ResetAsync(Ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void SkipIfUnavailable() => Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");

    protected static async Task<long> ScalarAsync(DbContext context, string sql)
    {
        await context.Database.OpenConnectionAsync(Ct);
        try
        {
            await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Reset_empties_chains_self_references_and_cycles()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v > 0);

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
    }

    [Fact]
    public async Task Reset_restarts_identity_on_used_and_never_used_tables()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await context.SeedAsync(ShopData.Customer(), ShopData.Customer(), ShopData.Customer());

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await context.SeedOneAsync(ShopData.Customer(), Ct)).Id.ShouldBe(1);

        // Categories has not had a row since the database was created.
        (await context.SeedOneAsync(new Category { Name = "First" }, Ct)).Id.ShouldBe(1);

        await context.Database.ResetDataAsync(cancellationToken: Ct);
        (await context.SeedOneAsync(new Category { Name = "Again" }, Ct)).Id.ShouldBe(1);
    }

    [Fact]
    public async Task Reset_continues_identity_when_asked()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await context.SeedAsync(ShopData.Customer(), ShopData.Customer());

        await context.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct);

        (await context.SeedOneAsync(ShopData.Customer(), Ct)).Id.ShouldBeGreaterThan(2);
    }

    [Fact]
    public async Task Reset_keeps_excluded_tables()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);

        await context.Database.ResetDataAsync(o => o.ExcludeEntity<Product>().ExcludeTable("Tags"), Ct);

        var counts = await ShopData.CountsAsync(context);
        counts["Products"].ShouldBe(2);
        counts["Tags"].ShouldBe(1);
        counts["OrderLines"].ShouldBe(0);
        counts["Teams"].ShouldBe(0);
    }

    [Fact]
    public async Task Reset_runs_synchronously_too()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);

        context.Database.ResetData();

        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
        (await context.SeedOneAsync(ShopData.Customer(), Ct)).Id.ShouldBe(1);
    }

    [Fact]
    public async Task Reset_leaves_foreign_keys_enforced()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ResetDataAsync(cancellationToken: Ct);

        context.Add(new Player { Name = "Orphan", TeamId = 999 });
        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));

        await AssertConstraintsRestoredAsync(context);
    }

    /// <summary>Engine-specific evidence that the reset put everything back.</summary>
    protected virtual Task AssertConstraintsRestoredAsync(ShopContext context) => Task.CompletedTask;

    [Fact]
    public async Task An_isolated_scope_shares_one_transaction_and_rolls_it_back()
    {
        SkipIfUnavailable();

        await using (var scope = await Fixture.Database.BeginIsolatedScopeAsync(cancellationToken: Ct))
        {
            var arrange = scope.CreateContext();
            var assert = scope.CreateContext();

            var customer = await arrange.SeedOneAsync(ShopData.Customer("Alice"), Ct);
            (await assert.Customers.SingleAsync(c => c.Id == customer.Id, Ct)).Name.ShouldBe("Alice");
        }

        await using var after = Fixture.CreateContext();
        (await after.Customers.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_reset_inside_an_isolated_scope_is_rolled_back_with_it()
    {
        SkipIfUnavailable();
        await using (var seed = Fixture.CreateContext())
        {
            await ShopData.SeedEverythingAsync(seed);
        }

        await using (var scope = await Fixture.Database.BeginIsolatedScopeAsync(cancellationToken: Ct))
        {
            var context = scope.CreateContext();
            await context.Database.ResetDataAsync(cancellationToken: Ct);
            (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
        }

        await using var after = Fixture.CreateContext();
        (await ShopData.CountsAsync(after)).Values.ShouldAllBe(v => v > 0);
    }

    [Fact]
    public async Task Built_graphs_insert_on_the_engine()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();

        var lines = await context.CreateManyAsync<OrderLine>(20, cancellationToken: Ct);
        var customers = await context.CreateManyAsync<Customer>(20, fillOptional: true, cancellationToken: Ct);

        lines.ShouldAllBe(l => l.Id > 0 && l.Order.Customer.Id > 0);
        customers.ShouldAllBe(c => c.Id > 0);
        (await context.Customers.CountAsync(Ct)).ShouldBe(40);
        (await context.Customers.FirstAsync(Ct)).RowStamp.ShouldBe("generated");
    }

    [Fact]
    public async Task Sql_capture_sees_the_engine_commands()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await context.CreateManyAsync<Order>(3, cancellationToken: Ct);
        using var sql = Fixture.Database.CaptureSql();

        // A projection: Oracle's MySQL provider cannot read a DateOnly column back, which is no
        // concern of the capture's.
        var rows = await context.Orders.Select(o => new { o.Id, o.Customer.Name }).ToListAsync(Ct);

        rows.Count.ShouldBe(3);
        sql.ShouldHaveExecuted(1).ShouldContain("JOIN");
    }
}

// One collection per engine, so each gets its own container and the engines run in parallel. The
// Engine trait is what lets CI shard the suite, or skip Docker entirely with a filter.

[CollectionDefinition("testing-postgres")]
public sealed class PostgreSqlCollectionDefinition : ICollectionFixture<PostgreSqlFixture>;

[CollectionDefinition("testing-sqlserver")]
public sealed class SqlServerCollectionDefinition : ICollectionFixture<SqlServerFixture>;

[CollectionDefinition("testing-mysql")]
public sealed class MySqlCollectionDefinition : ICollectionFixture<MySqlFixture>;

[Trait("Engine", "postgres17")]
[Collection("testing-postgres")]
public sealed class PostgreSqlEngineTests(PostgreSqlFixture fixture) : EngineTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("testing-sqlserver")]
public sealed class SqlServerEngineTests(SqlServerFixture fixture) : EngineTests(fixture)
{
    /// <summary>The cyclic tables' constraints are back on, and trusted — re-checked, not merely re-enabled.</summary>
    protected override async Task AssertConstraintsRestoredAsync(ShopContext context)
        => (await ScalarAsync(context, "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_disabled = 1 OR is_not_trusted = 1"))
            .ShouldBe(0);
}

[Trait("Engine", "mysql")]
[Collection("testing-mysql")]
public sealed class MySqlEngineTests(MySqlFixture fixture) : EngineTests(fixture)
{
    protected override async Task AssertConstraintsRestoredAsync(ShopContext context)
        => (await ScalarAsync(context, "SELECT @@FOREIGN_KEY_CHECKS")).ShouldBe(1);
}
