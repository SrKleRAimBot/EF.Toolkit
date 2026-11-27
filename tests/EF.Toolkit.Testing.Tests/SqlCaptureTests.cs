using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Testing.Tests;

public sealed class SqlCaptureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_commands_inside_the_capture_are_recorded()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await context.Customers.CountAsync(Ct);

        var sql = db.CaptureSql();
        await context.Customers.ToListAsync(Ct);
        sql.Dispose();
        await context.Products.ToListAsync(Ct);

        sql.ShouldHaveExecuted(1).ShouldContain("FROM \"Customers\"").ShouldNotContain("Products");
        sql.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_buffer_filled_again_after_the_command_does_not_rewrite_what_was_recorded()
    {
        // The parameter's byte[] was kept by reference: filled again for the next command, the first
        // command's recorded value changed with it.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        using var sql = db.CaptureSql();

        var buffer = new byte[] { 1 };
        await context.Database.SqlQueryRaw<int>("SELECT length({0}) AS \"Value\"", buffer).SingleAsync(Ct);
        buffer[0] = 2;
        await context.Database.SqlQueryRaw<int>("SELECT length({0}) AS \"Value\"", buffer).SingleAsync(Ct);

        sql.Commands[0].Parameters.ShouldHaveSingleItem().Value.ShouldBe(new byte[] { 1 });
        sql.Commands[1].Parameters.ShouldHaveSingleItem().Value.ShouldBe(new byte[] { 2 });
    }

    [Fact]
    public async Task Kind_text_and_parameters_are_recorded()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        using var sql = db.CaptureSql();

        var name = "Alice";
        await context.Customers.Where(c => c.Name == name).ToListAsync(Ct);
        await context.GetService<IRelationalDatabaseCreator>().HasTablesAsync(Ct);
        await context.Customers.ExecuteDeleteAsync(Ct);
        _ = context.Customers.Where(c => c.Name == "sync").ToList();

        // Every LINQ query is a reader, aggregates included; EF's own catalog checks are scalars.
        var commands = sql.Commands;
        commands.Select(c => c.Kind).ShouldBe(
            [SqlCommandKind.Reader, SqlCommandKind.Scalar, SqlCommandKind.NonQuery, SqlCommandKind.Reader]);
        commands.ShouldAllBe(c => c.Succeeded);
        commands[0].Parameters.ShouldHaveSingleItem().Value.ShouldBe("Alice");
        commands[0].ToString().ShouldBe(commands[0].CommandText);
        commands[0].Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task Null_parameters_are_reported_as_null()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        using var sql = db.CaptureSql();

        await context.SeedOneAsync(ShopData.Customer(), Ct);

        sql.Commands.SelectMany(c => c.Parameters).ShouldContain(p => p.Value == null);
    }

    [Fact]
    public async Task Failed_commands_are_recorded_as_failed()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        using var sql = db.CaptureSql();

        await Should.ThrowAsync<Exception>(() => context.Database.ExecuteSqlRawAsync("SELECT * FROM \"Missing\"", Ct));
        Should.Throw<Exception>(() => context.Database.ExecuteSqlRaw("SELECT * FROM \"AlsoMissing\""));

        sql.Commands.Count.ShouldBe(2);
        sql.Commands.ShouldAllBe(c => !c.Succeeded);
    }

    [Fact]
    public async Task Synchronous_commands_of_every_kind_are_recorded()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        using var context = db.CreateContext();
        using var sql = db.CaptureSql();

        context.GetService<IRelationalDatabaseCreator>().HasTables();
        context.Customers.ExecuteDelete();
        _ = context.Customers.ToList();

        sql.Commands.Select(c => c.Kind).ShouldBe([SqlCommandKind.Scalar, SqlCommandKind.NonQuery, SqlCommandKind.Reader]);
    }

    [Fact]
    public async Task An_N_plus_1_is_caught()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using (var arrange = db.CreateContext())
        {
            await arrange.CreateManyAsync<Order>(3, cancellationToken: Ct);
        }

        await using var context = db.CreateContext();
        using var sql = db.CaptureSql();

        foreach (var order in await context.Orders.ToListAsync(Ct))
        {
            await context.Entry(order).Reference(o => o.Customer).LoadAsync(Ct);
        }

        var ex = Should.Throw<EFToolkitAssertionException>(() => sql.ShouldHaveExecutedAtMost(1));
        ex.Message.ShouldStartWith("Expected at most 1 command to be executed, but 4 commands were.");
        ex.Message.ShouldContain("[4] SELECT");

        sql.Clear();
        await context.Orders.Include(o => o.Customer).ToListAsync(Ct);
        sql.ShouldHaveExecutedAtMost(1).ShouldHaveExecuted(1);
    }

    [Fact]
    public async Task A_context_capture_ignores_other_contexts_on_the_same_database()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var mine = db.CreateContext();
        await using var other = db.CreateContext();
        using var sql = mine.CaptureSql();

        await other.Products.ToListAsync(Ct);
        await mine.Customers.ToListAsync(Ct);

        sql.ShouldHaveExecuted(1).ShouldContain("Customers");
    }

    [Fact]
    public async Task A_database_capture_sees_every_context_of_that_database_and_no_other()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var elsewhere = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        using var sql = db.CaptureSql();

        await using (var first = db.CreateContext())
        {
            await first.Customers.ToListAsync(Ct);
        }

        await using (var scope = await db.BeginIsolatedScopeAsync(cancellationToken: Ct))
        {
            await scope.CreateContext().Products.ToListAsync(Ct);
        }

        await using (var foreign = elsewhere.CreateContext())
        {
            await foreign.Categories.ToListAsync(Ct);
        }

        sql.ShouldHaveExecuted(2).ShouldContain("Customers").ShouldContain("Products").ShouldNotContain("Categories");
    }

    [Fact]
    public async Task Parallel_tests_on_one_database_do_not_bleed_into_each_other()
    {
        var file = Path.Combine(Path.GetTempPath(), $"eftoolkit-testing-{Guid.NewGuid():N}.db");
        try
        {
            // A file database, so each parallel flow can have a connection of its own.
            await using var db = await TestDatabase<ShopContext>.CreateAsync(
                o => o.UseSqlite($"Data Source={file}"),
                cancellationToken: Ct);

            async Task<SqlCapture> Test(string table, int queries)
            {
                await Task.Yield();
                var sql = db.CaptureSql();
                await using var context = db.CreateContext();
                for (var i = 0; i < queries; i++)
                {
                    // The table name is one of two constants, not input.
#pragma warning disable EF1002
                    await context.Database.ExecuteSqlRawAsync($"SELECT COUNT(*) FROM \"{table}\"", Ct);
#pragma warning restore EF1002
                    await Task.Delay(1, Ct);
                }

                sql.Dispose();
                return sql;
            }

            var results = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(i => Task.Run(() => Test(i % 2 == 0 ? "Customers" : "Products", 10 + i), Ct)));

            for (var i = 0; i < results.Length; i++)
            {
                results[i].ShouldHaveExecuted(10 + i);
                results[i].ShouldNotContain(i % 2 == 0 ? "Products" : "Customers");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_capture_started_in_a_parent_flow_sees_the_tasks_it_starts()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        using var sql = db.CaptureSql();

        await Task.Run(async () =>
        {
            await using var context = db.CreateContext();
            await context.Customers.ToListAsync(Ct);
        }, Ct);

        sql.ShouldHaveExecuted(1);
    }

    [Fact]
    public async Task A_capture_started_elsewhere_is_not_seen_here()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        var sql = await Task.Run(db.CaptureSql, Ct);

        await using var context = db.CreateContext();
        await context.Customers.ToListAsync(Ct);

        sql.ShouldHaveExecuted(0);
        sql.Dispose();
    }

    [Fact]
    public async Task Nested_captures_each_record_their_own_span()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        using var outer = db.CaptureSql();

        await context.Customers.ToListAsync(Ct);
        using (var inner = context.CaptureSql())
        {
            await context.Products.ToListAsync(Ct);
            inner.ShouldHaveExecuted(1);
        }

        await context.Tags.ToListAsync(Ct);
        outer.ShouldHaveExecuted(3);
    }

    [Fact]
    public async Task UseSqlCapture_makes_any_context_capturable()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        var options = new DbContextOptionsBuilder<ShopContext>().UseSqlite(connection).UseSqlCapture().Options;
        await using var context = new ShopContext(options);
        await context.Database.EnsureCreatedAsync(Ct);

        using var sql = context.CaptureSql();
        using var global = SqlCapture.Start();
        await context.Customers.ToListAsync(Ct);

        sql.ShouldHaveExecuted(1);
        global.ShouldHaveExecuted(1);
    }

    [Fact]
    public void The_untyped_options_builder_registers_it_too()
    {
        var builder = new DbContextOptionsBuilder().UseSqlite("Data Source=:memory:").UseSqlCapture();

        builder.Options.FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()!
            .Interceptors!.ShouldContain(SqlCaptureInterceptor.Shared);
    }

    [Fact]
    public void A_context_without_the_interceptor_explains_how_to_add_it()
    {
        using var context = Contexts.Sqlite();

        Should.Throw<InvalidOperationException>(() => context.CaptureSql()).Message.ShouldContain("UseSqlCapture()");
    }

    [Fact]
    public async Task Assertion_failures_say_what_was_expected_and_list_the_commands()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        using var sql = db.CaptureSql();

        Should.Throw<EFToolkitAssertionException>(() => sql.ShouldHaveExecuted(1))
            .Message.ShouldBe("Expected 1 command to be executed, but 0 commands were.");
        Should.Throw<EFToolkitAssertionException>(() => sql.ShouldContain("anything"))
            .Message.ShouldBe("Expected a command containing \"anything\", but none of the 0 commands did.");

        await context.Customers.ToListAsync(Ct);

        Should.Throw<EFToolkitAssertionException>(() => sql.ShouldHaveExecuted(2))
            .Message.ShouldStartWith("Expected 2 commands to be executed, but 1 command was.");
        Should.Throw<EFToolkitAssertionException>(() => sql.ShouldNotContain("customers"))
            .Message.ShouldStartWith("Expected no command containing \"customers\", but 1 command did.");
        Should.Throw<EFToolkitAssertionException>(() => sql.ShouldContain("customers", StringComparison.Ordinal))
            .Message.ShouldContain("[1] SELECT");
        sql.ShouldNotContain("customers", StringComparison.Ordinal);
        Should.Throw<ArgumentNullException>(() => sql.ShouldContain(null!));
        Should.Throw<ArgumentNullException>(() => sql.ShouldNotContain(null!));
        Should.Throw<ArgumentNullException>(() => SqlCaptureExtensions.CaptureSql(null!));
        Should.Throw<ArgumentNullException>(() => SqlCaptureExtensions.UseSqlCapture(null!));
        Should.Throw<ArgumentNullException>(() => SqlCaptureExtensions.UseSqlCapture<ShopContext>(null!));
    }
}
