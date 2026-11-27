using System.Data;
using EFToolkit.Testing.Tests.Infrastructure;
using EFToolkit.Testing.Tests.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Testing.Tests;

public sealed class TestDatabaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"eftoolkit-testing-{Guid.NewGuid():N}.db");

    [Fact]
    public void The_synchronous_factory_does_not_deadlock_an_async_seeder_under_a_single_threaded_context()
    {
        // A UI-style context that only runs continuations when pumped — and the creating thread, blocked
        // in Create(), never pumps. A seeder awaiting without ConfigureAwait(false) used to wait forever.
        Exception? failure = null;
        var created = false;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverPumpedContext());
            try
            {
                using var db = SqliteTestDatabase<ShopContext>.Create(o => o.Seed(async (context, _) =>
                {
                    await Task.Yield();
                    context.Tags.Add(new Tag { Code = "y", Label = "yielded" });
                }));
                using var context = db.CreateContext();
                created = context.Tags.Count() == 1;
            }
#pragma warning disable CA1031 // Surfaced on the test thread below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        });

        // Background, so that if the deadlock ever returns the timeout fails this test instead of the
        // blocked thread keeping the test host alive.
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue("Create() deadlocked on the async seeder.");
        failure.ShouldBeNull();
        created.ShouldBeTrue();
    }

    private sealed class NeverPumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // Queued for a pump that never comes.
        }
    }

    [Fact]
    public async Task An_in_memory_database_is_created_with_its_schema_and_shared_between_contexts()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);

        await using (var first = db.CreateContext())
        {
            await first.SeedOneAsync(ShopData.Customer("Alice"), Ct);
        }

        await using var second = db.CreateContext();
        (await second.Customers.SingleAsync(Ct)).Name.ShouldBe("Alice");
    }

    [Fact]
    public async Task Each_context_is_a_new_instance()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var first = db.CreateContext();
        await using var second = db.CreateContext();

        first.ShouldNotBeSameAs(second);
        first.Database.GetDbConnection().ShouldBeSameAs(db.Connection);
        second.Database.GetDbConnection().ShouldBeSameAs(db.Connection);
    }

    [Fact]
    public async Task Two_in_memory_databases_are_independent()
    {
        await using var one = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var two = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);

        await using (var context = one.CreateContext())
        {
            await context.SeedOneAsync(ShopData.Customer(), Ct);
        }

        await using var other = two.CreateContext();
        (await other.Customers.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Disposing_the_database_closes_the_connection_and_refuses_new_contexts()
    {
        var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        var connection = db.Connection;
        connection.State.ShouldBe(ConnectionState.Open);

        await db.DisposeAsync();
        await db.DisposeAsync();

        connection.State.ShouldBe(ConnectionState.Closed);
        Should.Throw<ObjectDisposedException>(() => db.CreateContext());
    }

    [Fact]
    public void The_synchronous_factory_and_dispose_work_too()
    {
        var db = SqliteTestDatabase<ShopContext>.Create(o => o.Seed(c => c.Tags.Add(new Tag { Code = "s", Label = "s" })));

        using (var context = db.CreateContext())
        {
            context.Tags.Count().ShouldBe(1);
        }

        db.Dispose();
        db.Dispose();
        db.Connection.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public async Task Extra_options_are_applied_after_the_provider()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(
            o => o
                .ConfigureOptions(b => b.EnableSensitiveDataLogging())
                .ConfigureOptions(b => b.EnableDetailedErrors()),
            Ct);

        var core = db.Options.FindExtension<CoreOptionsExtension>()!;
        core.IsSensitiveDataLoggingEnabled.ShouldBeTrue();
        core.DetailedErrorsEnabled.ShouldBeTrue();
        core.Interceptors!.OfType<SqlCaptureInterceptor>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Seeders_run_once_in_order_on_fresh_contexts_and_are_saved()
    {
        var contexts = new List<ShopContext>();
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(
            o => o
                .Seed(c =>
                {
                    contexts.Add(c);
                    c.Tags.Add(new Tag { Code = "a", Label = "first" });
                })
                .Seed(async (c, ct) =>
                {
                    contexts.Add(c);
                    (await c.Tags.CountAsync(ct)).ShouldBe(1);
                    c.Tags.Add(new Tag { Code = "b", Label = "second" });
                })
                .Seed(new TagSeeder()),
            Ct);

        await using var context = db.CreateContext();
        (await context.Tags.OrderBy(t => t.Code).Select(t => t.Code).ToListAsync(Ct)).ShouldBe(["a", "b", "c"]);
        contexts.Count.ShouldBe(2);
        contexts[0].ShouldNotBeSameAs(contexts[1]);
    }

    [Fact]
    public async Task ResetAsync_empties_the_tables_and_seeds_again()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(
            o => o.Seed(new TagSeeder()).ResetWith(r => r.ExcludeEntity<Product>()),
            Ct);

        await using (var context = db.CreateContext())
        {
            await context.SeedAsync(ShopData.Customer(), ShopData.Product());
        }

        await db.ResetAsync(Ct);

        await using var after = db.CreateContext();
        (await after.Tags.Select(t => t.Code).ToListAsync(Ct)).ShouldBe(["c"]);
        (await after.Customers.CountAsync(Ct)).ShouldBe(0);
        (await after.Products.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_factory_builds_contexts_that_need_more_than_options()
    {
        await using var db = await SqliteTestDatabase<FactoryOnlyContext>.CreateAsync(
            o => o.UseContextFactory(options => new FactoryOnlyContext(options, "made")),
            Ct);

        await using var context = db.CreateContext();
        context.Marker.ShouldBe("made");
    }

    [Fact]
    public async Task Without_a_factory_a_context_needing_more_than_options_is_explained()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => SqliteTestDatabase<FactoryOnlyContext>.CreateAsync(cancellationToken: Ct));

        ex.Message.ShouldContain("FactoryOnlyContext");
        ex.Message.ShouldContain("UseContextFactory");
    }

    [Fact]
    public async Task A_context_taking_untyped_options_is_constructed()
    {
        await using var db = await SqliteTestDatabase<UntypedOptionsContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();

        (await context.Tags.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_constructor_failure_surfaces_unwrapped()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => SqliteTestDatabase<ThrowingContext>.CreateAsync(cancellationToken: Ct));

        ex.Message.ShouldBe("constructor says no");
    }

    [Fact]
    public async Task A_failing_seeder_fails_creation()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => SqliteTestDatabase<ShopContext>.CreateAsync(
            o => o.Seed(_ => throw new InvalidOperationException("seed failed")),
            Ct));
        Should.Throw<InvalidOperationException>(() => SqliteTestDatabase<ShopContext>.Create(
            o => o.Seed(_ => throw new InvalidOperationException("seed failed"))));
    }

    [Fact]
    public async Task The_None_strategy_creates_no_schema()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(
            o => o.CreateWith(DatabaseCreationStrategy.None),
            Ct);
        await using var context = db.CreateContext();

        await Should.ThrowAsync<Exception>(() => context.Customers.CountAsync(Ct));
    }

    [Fact]
    public async Task Migrations_create_the_schema_they_describe()
    {
        await using var db = await SqliteTestDatabase<MigratedContext>.CreateAsync(o => o.UseMigrations(), Ct);
        await using var context = db.CreateContext();

        (await context.Database.GetAppliedMigrationsAsync(Ct)).ShouldBe(["20260101000000_Initial"]);
        await context.SeedOneAsync(new Widget { Name = "w" }, Ct);
        (await context.Widgets.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public void Migrations_run_synchronously_too()
    {
        using var db = SqliteTestDatabase<MigratedContext>.Create(o => o.UseMigrations());
        using var context = db.CreateContext();

        context.Database.GetAppliedMigrations().ShouldBe(["20260101000000_Initial"]);
    }

    [Fact]
    public async Task A_real_engine_database_is_created_from_the_provider_delegate()
    {
        var file = TempFile();
        try
        {
            await using (var db = await TestDatabase<ShopContext>.CreateAsync(
                o => o.UseSqlite($"Data Source={file};Pooling=False"),
                o => o.Seed(new TagSeeder()),
                Ct))
            {
                await using var context = db.CreateContext();
                (await context.Tags.CountAsync(Ct)).ShouldBe(1);
                await context.SeedOneAsync(ShopData.Customer(), Ct);
            }

            File.Exists(file).ShouldBeTrue();

            // Without DeleteExisting the schema and data from before are kept, and seeding adds to it.
            await using (var again = await TestDatabase<ShopContext>.CreateAsync(
                o => o.UseSqlite($"Data Source={file};Pooling=False"),
                cancellationToken: Ct))
            {
                await using var context = again.CreateContext();
                (await context.Customers.CountAsync(Ct)).ShouldBe(1);
            }

            await using (var fresh = await TestDatabase<ShopContext>.CreateAsync(
                o => o.UseSqlite($"Data Source={file};Pooling=False"),
                o => o.DeleteExisting(),
                Ct))
            {
                await using var context = fresh.CreateContext();
                (await context.Customers.CountAsync(Ct)).ShouldBe(0);
            }

            using (var sync = TestDatabase<ShopContext>.Create(
                o => o.UseSqlite($"Data Source={file};Pooling=False"),
                o => o.DeleteExisting().Seed(new TagSeeder())))
            {
                using var context = sync.CreateContext();
                context.Tags.Count().ShouldBe(1);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Should.Throw<ArgumentNullException>(() => TestDatabase<ShopContext>.Create(null!));
        Should.Throw<ArgumentNullException>(() => SqliteTestDatabase<ShopContext>.Create(o => o.ConfigureOptions(null!)));
        Should.Throw<ArgumentNullException>(() => SqliteTestDatabase<ShopContext>.Create(o => o.Seed((ISeeder<ShopContext>)null!)));
        Should.Throw<ArgumentNullException>(() => SqliteTestDatabase<ShopContext>.Create(o => o.Seed((Action<ShopContext>)null!)));
        Should.Throw<ArgumentNullException>(() => SqliteTestDatabase<ShopContext>.Create(o => o.Seed((Func<ShopContext, CancellationToken, Task>)null!)));
        Should.Throw<ArgumentNullException>(() => SqliteTestDatabase<ShopContext>.Create(o => o.UseContextFactory(null!)));
        Should.Throw<ArgumentNullException>(() => SqliteTestDatabase<ShopContext>.Create(o => o.ResetWith(null!)));
    }

    private sealed class TagSeeder : ISeeder<ShopContext>
    {
        public Task SeedAsync(ShopContext context, CancellationToken cancellationToken)
        {
            context.Tags.Add(new Tag { Code = "c", Label = "from a seeder" });
            return Task.CompletedTask;
        }
    }

    public sealed class ThrowingContext : DbContext
    {
        public ThrowingContext(DbContextOptions<ThrowingContext> options)
            : base(options)
            => throw new InvalidOperationException("constructor says no");
    }
}
