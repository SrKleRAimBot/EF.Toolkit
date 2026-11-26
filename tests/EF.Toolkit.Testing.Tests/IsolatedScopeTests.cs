using System.Data;
using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Testing.Tests;

public sealed class IsolatedScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Everything_written_in_the_scope_is_rolled_back()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);

        await using (var scope = await db.BeginIsolatedScopeAsync(cancellationToken: Ct))
        {
            await using var context = scope.CreateContext();
            await context.SeedAsync(ShopData.Customer(), ShopData.Customer());
            (await context.Customers.CountAsync(Ct)).ShouldBe(2);
        }

        await using var after = db.CreateContext();
        (await after.Customers.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Contexts_in_one_scope_see_each_others_writes()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var scope = await db.BeginIsolatedScopeAsync(cancellationToken: Ct);

        var arrange = scope.CreateContext();
        var act = scope.CreateContext();

        var customer = await arrange.SeedOneAsync(ShopData.Customer("Alice"), Ct);
        (await act.Customers.SingleAsync(c => c.Id == customer.Id, Ct)).Name.ShouldBe("Alice");

        arrange.Database.CurrentTransaction.ShouldNotBeNull();
        act.Database.GetDbConnection().ShouldBeSameAs(scope.Connection);
        act.Database.CurrentTransaction!.GetDbTransaction().ShouldBeSameAs(scope.Transaction);
    }

    [Fact]
    public async Task Seed_data_committed_before_the_scope_survives_it()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(
            o => o.Seed(c => c.Tags.Add(new Tag { Code = "ref", Label = "reference" })),
            Ct);

        for (var test = 0; test < 3; test++)
        {
            await using var scope = await db.BeginIsolatedScopeAsync(cancellationToken: Ct);
            var context = scope.CreateContext();
            (await context.Tags.CountAsync(Ct)).ShouldBe(1);
            await context.SeedOneAsync(new Tag { Code = $"t{test}", Label = "scoped" }, Ct);
        }

        await using var after = db.CreateContext();
        (await after.Tags.Select(t => t.Code).ToListAsync(Ct)).ShouldBe(["ref"]);
    }

    [Fact]
    public async Task A_scope_on_a_file_database_rolls_back_too()
    {
        var file = Path.Combine(Path.GetTempPath(), $"eftoolkit-testing-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = await TestDatabase<ShopContext>.CreateAsync(
                o => o.UseSqlite($"Data Source={file};Pooling=False"),
                cancellationToken: Ct);

            await using (var scope = await db.BeginIsolatedScopeAsync(IsolationLevel.Serializable, Ct))
            {
                var first = scope.CreateContext();
                var second = scope.CreateContext();
                await first.SeedOneAsync(ShopData.Customer(), Ct);
                (await second.Customers.CountAsync(Ct)).ShouldBe(1);
                scope.Transaction.IsolationLevel.ShouldBe(IsolationLevel.Serializable);
            }

            await using var after = db.CreateContext();
            (await after.Customers.CountAsync(Ct)).ShouldBe(0);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task The_synchronous_scope_rolls_back()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);

        using (var scope = db.BeginIsolatedScope())
        {
            var context = scope.CreateContext();
            context.Seed(ShopData.Customer());
            context.Customers.Count().ShouldBe(1);
            scope.Dispose();
        }

        using (var scope = db.BeginIsolatedScope(IsolationLevel.Serializable))
        {
            scope.Transaction.IsolationLevel.ShouldBe(IsolationLevel.Serializable);
        }

        await using var after = db.CreateContext();
        (await after.Customers.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task The_scope_disposes_its_contexts_and_refuses_new_ones()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        var scope = await db.BeginIsolatedScopeAsync(cancellationToken: Ct);
        var context = scope.CreateContext();

        await scope.DisposeAsync();
        await scope.DisposeAsync();

        Should.Throw<ObjectDisposedException>(() => context.Customers.Count());
        Should.Throw<ObjectDisposedException>(() => scope.CreateContext());

        // The database's own held connection is not the scope's to close.
        db.Connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public async Task A_context_disposed_early_does_not_end_the_scope()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var scope = await db.BeginIsolatedScopeAsync(cancellationToken: Ct);

        await using (var early = scope.CreateContext())
        {
            await early.SeedOneAsync(ShopData.Customer(), Ct);
        }

        var later = scope.CreateContext();
        (await later.Customers.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_disposed_database_refuses_a_scope()
    {
        var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await db.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() => db.BeginIsolatedScopeAsync(cancellationToken: Ct));
        Should.Throw<ObjectDisposedException>(() => db.BeginIsolatedScope());
    }

    [Fact]
    public async Task A_scope_that_cannot_begin_releases_its_root_context()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var outer = await db.BeginIsolatedScopeAsync(cancellationToken: Ct);

        // SQLite has one connection here, and it already carries the outer scope's transaction.
        await Should.ThrowAsync<InvalidOperationException>(() => db.BeginIsolatedScopeAsync(cancellationToken: Ct));
        Should.Throw<InvalidOperationException>(() => db.BeginIsolatedScope());
    }
}
