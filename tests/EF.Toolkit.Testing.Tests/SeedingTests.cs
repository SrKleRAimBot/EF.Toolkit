using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing.Tests;

public sealed class SeedingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeding_saves_populates_keys_and_clears_the_tracker()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        var alice = ShopData.Customer("Alice");
        var product = ShopData.Product();

        var seeded = await context.SeedAsync(alice, product);

        seeded.ShouldBe([alice, product]);
        alice.Id.ShouldBeGreaterThan(0);
        product.Id.ShouldBeGreaterThan(0);
        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_seed_that_fails_while_attaching_leaves_nothing_tracked(bool async)
    {
        // AddRange tracked the first of two entities with one key and threw on the second, before the
        // cleanup: the next SaveChanges inserted the first.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        var first = ShopData.Customer("First");
        var second = ShopData.Customer("Second");
        first.Id = second.Id = 42;

        if (async)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => context.SeedAsync(first, second));
        }
        else
        {
            Should.Throw<InvalidOperationException>(() => context.Seed(first, second));
        }

        context.ChangeTracker.Entries().ShouldBeEmpty();
        await context.SaveChangesAsync(Ct);
        (await context.Customers.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_query_after_seeding_reads_the_database_not_the_seeded_instance()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        var alice = await context.SeedOneAsync(ShopData.Customer("Alice"), Ct);

        // Changed in memory only. Left tracked, the query below would hand this very instance back.
        alice.Name = "Changed";

        var loaded = await context.Customers.SingleAsync(Ct);
        loaded.ShouldNotBeSameAs(alice);
        loaded.Name.ShouldBe("Alice");
    }

    [Fact]
    public async Task Graphs_are_inserted_whole()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        var order = ShopData.Order(ShopData.Customer(), ShopData.Product(), ShopData.Product());

        await context.SeedAsync(order);

        order.Customer.Id.ShouldBeGreaterThan(0);
        order.Lines.ShouldAllBe(l => l.Id > 0 && l.OrderId == order.Id);
        (await context.OrderLines.CountAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task The_generic_overload_returns_the_typed_list()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        var customers = new List<Customer> { ShopData.Customer(), ShopData.Customer() };

        var seeded = await context.SeedAsync(customers, Ct);

        seeded.ShouldBeSameAs(customers);
        seeded.Select(c => c.Id).ShouldBe([1, 2]);
        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lazy_sequence_is_materialised_once()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        var evaluations = 0;
        IEnumerable<Customer> Customers()
        {
            evaluations++;
            yield return ShopData.Customer();
        }

        var seeded = await context.SeedAsync(Customers(), Ct);

        evaluations.ShouldBe(1);
        seeded.ShouldHaveSingleItem().Id.ShouldBe(1);
    }

    [Fact]
    public async Task The_synchronous_overloads_do_the_same()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();

        var objects = context.Seed(ShopData.Customer(), ShopData.Product());
        var typed = context.Seed(new[] { ShopData.Customer() }.AsEnumerable());

        objects.Count.ShouldBe(2);
        typed.ShouldHaveSingleItem().Id.ShouldBe(2);
        context.ChangeTracker.Entries().ShouldBeEmpty();
        (await context.Customers.CountAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_seed_still_clears_the_tracker()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await context.SeedOneAsync(new Tag { Code = "dup", Label = "first" }, Ct);

        await Should.ThrowAsync<DbUpdateException>(() => context.SeedOneAsync(new Tag { Code = "dup", Label = "again" }, Ct));
        context.ChangeTracker.Entries().ShouldBeEmpty();

        Should.Throw<DbUpdateException>(() => context.Seed(new Tag { Code = "dup", Label = "again" }));
        context.ChangeTracker.Entries().ShouldBeEmpty();

        // Nothing left behind to fail the next save.
        await context.SeedOneAsync(new Tag { Code = "ok", Label = "fine" }, Ct);
    }

    [Fact]
    public async Task Null_arguments_are_refused()
    {
        using var context = Contexts.Sqlite();

        Should.Throw<ArgumentNullException>(() => DbContextSeedingExtensions.Seed(null!, new object()));
        Should.Throw<ArgumentNullException>(() => context.Seed((object[])null!));
        Should.Throw<ArgumentNullException>(() => context.Seed((IEnumerable<Tag>)null!));
        Should.Throw<ArgumentNullException>(() => DbContextSeedingExtensions.Seed<Tag>(null!, []));
        await Should.ThrowAsync<ArgumentNullException>(() => DbContextSeedingExtensions.SeedAsync(null!, new object()));
        await Should.ThrowAsync<ArgumentNullException>(() => context.SeedAsync((object[])null!));
        await Should.ThrowAsync<ArgumentNullException>(() => context.SeedAsync((IEnumerable<Tag>)null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => DbContextSeedingExtensions.SeedAsync<Tag>(null!, [], Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => context.SeedOneAsync<Tag>(null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => DbContextSeedingExtensions.SeedOneAsync(null!, new Tag(), Ct));
    }
}
