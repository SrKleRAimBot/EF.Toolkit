using System.Data;
using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EFToolkit.Sql.Integration;

[Trait("Engine", "postgres17")]
[Collection("sql-postgres17")]
public sealed class RoutineTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly StoredProcedureDefinition AddOrder = new(
        "add_order",
        [new("tenant", "integer"), new("total", "numeric(10,2)"), new("note", "text")],
        """
        BEGIN
          INSERT INTO "Orders" ("TenantId", "Total", "Note") VALUES (tenant, total, note);
        END;
        """);

    private static readonly StoredProcedureDefinition DoubleIt = new(
        "double_it",
        [new("value", "integer", SqlParameterDirection.InOut)],
        "BEGIN value := value * 2; END;");

    // A body that itself contains $$ — the case that would end a naively quoted body early.
    private static readonly FunctionDefinition Greeting = FunctionDefinition.Scalar(
        "greeting",
        [new("name", "text")],
        "text",
        "SELECT $$Hello, $$ || name",
        language: "sql",
        characteristics: "IMMUTABLE");

    private static readonly FunctionDefinition OrdersFor = FunctionDefinition.Table(
        "orders_for",
        [new("tenant", "integer")],
        [new("Id", "integer"), new("Total", "numeric")],
        """SELECT "Id", "Total" FROM "Orders" WHERE "TenantId" = tenant""",
        characteristics: "STABLE");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var context = fixture.CreateAdminContext();
        await context.Database.CreateDatabaseObjectsAsync(AddOrder, DoubleIt, Greeting, OrdersFor);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_procedure_with_a_reserved_parameter_name_is_created_and_called()
    {
        // `from` cannot be written bare as a parameter or a named argument; both are quoted.
        var since = new StoredProcedureDefinition(
            "orders_from",
            [new("from", "integer"), new("tenant", "integer")],
            """BEGIN INSERT INTO "Orders" ("TenantId", "Total", "Note") VALUES (tenant, "from", 'reserved'); END;""");

        await using var context = fixture.CreateAdminContext();
        await context.Database.CreateDatabaseObjectsAsync(since);
        await context.Database.ExecuteProcedureAsync("orders_from", new { From = 42, tenant = 5 }, cancellationToken: Token);

        (await context.Orders.SingleAsync(o => o.Note == "reserved", Token)).Total.ShouldBe(42m);
    }

    [Fact]
    public async Task Dropping_an_overload_that_was_never_created_leaves_its_sibling()
    {
        // By name alone, DROP FUNCTION IF EXISTS lookup dropped lookup(text), the only match.
        var text = FunctionDefinition.Scalar("lookup_kind", [new("v", "text")], "integer", "SELECT 1", language: "sql");
        var number = FunctionDefinition.Scalar("lookup_kind", [new("v", "integer")], "integer", "SELECT 2", language: "sql");
        var textProc = new StoredProcedureDefinition("touch_kind", [new("v", "text")], "BEGIN END;");
        var numberProc = new StoredProcedureDefinition("touch_kind", [new("v", "integer")], "BEGIN END;");

        await using var context = fixture.CreateAdminContext();
        await context.Database.CreateDatabaseObjectsAsync(text, textProc);
        try
        {
            await context.Database.DropDatabaseObjectsAsync(number, numberProc);

            (await context.Database.SqlQueryRaw<int>("SELECT lookup_kind('x'::text) AS \"Value\"").SingleAsync(Token)).ShouldBe(1);
            (await context.Database.SqlQueryRaw<long>(
                    "SELECT COUNT(*) AS \"Value\" FROM pg_proc WHERE proname = 'touch_kind'")
                .SingleAsync(Token)).ShouldBe(1);
        }
        finally
        {
            await context.Database.DropDatabaseObjectsAsync(text, textProc);
        }
    }

    [Fact]
    public async Task A_procedure_runs_with_its_arguments_bound_by_name()
    {
        await using var context = fixture.CreateAdminContext();

        // Out of declaration order on purpose: PostgreSQL's named notation matches them by name.
        await context.Database.ExecuteProcedureAsync("add_order", new { note = "it's", total = 9.5m, tenant = 1 }, cancellationToken: Token);
        await context.Database.ExecuteProcedureAsync(
            "add_order",
            new Dictionary<string, object?> { ["tenant"] = 2, ["total"] = 3m, ["note"] = null },
            cancellationToken: Token);

        var orders = await context.Orders.OrderBy(o => o.Id).ToListAsync(Token);
        orders.Select(o => (o.TenantId, o.Total, o.Note)).ShouldBe([(1, 9.5m, (string?)"it's"), (2, 3m, null)]);
    }

    [Fact]
    public async Task A_value_that_looks_like_sql_is_only_ever_data()
    {
        await using var context = fixture.CreateAdminContext();
        const string hostile = "x'); DROP TABLE \"Orders\"; --";

        await context.Database.ExecuteProcedureAsync("add_order", new { tenant = 1, total = 1m, note = hostile }, cancellationToken: Token);

        (await context.Orders.SingleAsync(Token)).Note.ShouldBe(hostile);
    }

    [Fact]
    public async Task QueryProcedure_reads_the_row_CALL_returns_for_inout_parameters()
    {
        await using var context = fixture.CreateAdminContext();

        var rows = await context.Database.QueryProcedureAsync<int>("double_it", new { value = 21 }, cancellationToken: Token);

        rows.ShouldBe([42]);
    }

    [Fact]
    public async Task A_DbParameter_passes_through_and_comes_back_with_the_output()
    {
        await using var context = fixture.CreateAdminContext();
        var value = new NpgsqlParameter("value", NpgsqlDbType.Integer) { Direction = ParameterDirection.InputOutput, Value = 5 };

        await context.Database.ExecuteProcedureAsync("double_it", new { value }, cancellationToken: Token);

        value.Value.ShouldBe(10);
    }

    [Fact]
    public async Task A_procedure_in_a_schema_is_called_by_its_qualified_name()
    {
        await using var context = fixture.CreateAdminContext();
        await PostgreSqlFixture.ExecuteAsync(fixture.AdminConnectionString, "CREATE SCHEMA IF NOT EXISTS \"Billing Ops\"");
        var procedure = new StoredProcedureDefinition("noop", null, "BEGIN END;", "Billing Ops");

        await context.Database.CreateDatabaseObjectsAsync(procedure);
        await context.Database.ExecuteProcedureAsync("noop", schema: "Billing Ops", cancellationToken: Token);

        await context.Database.DropDatabaseObjectsAsync(procedure);
        await Should.ThrowAsync<PostgresException>(() =>
            context.Database.ExecuteProcedureAsync("noop", schema: "Billing Ops", cancellationToken: Token));
    }

    [Fact]
    public async Task A_scalar_function_whose_body_contains_dollar_quotes()
    {
        await using var context = fixture.CreateAdminContext();

        var greeting = await context.Database
            .SqlQueryRaw<string>("SELECT greeting({0}) AS \"Value\"", "Ada")
            .SingleAsync(Token);

        greeting.ShouldBe("Hello, Ada");
    }

    [Fact]
    public async Task A_table_function_composes_with_LINQ_in_the_database()
    {
        await using var context = fixture.CreateAdminContext();
        context.Orders.AddRange(
            new Order { TenantId = 1, Total = 4m },
            new Order { TenantId = 1, Total = 12m },
            new Order { TenantId = 1, Total = 30m },
            new Order { TenantId = 2, Total = 50m });
        await context.SaveChangesAsync(Token);

        var query = context.Database.QueryFunction<OrderRow>("orders_for", new { tenant = 1 })
            .Where(o => o.Total > 5m)
            .OrderByDescending(o => o.Total);

        query.ToQueryString().ShouldContain("WHERE");
        (await query.Select(o => o.Total).ToListAsync(Token)).ShouldBe([30m, 12m]);
    }

    [Fact]
    public async Task Functions_and_procedures_can_be_dropped_and_recreated()
    {
        await using var context = fixture.CreateAdminContext();

        await context.Database.DropDatabaseObjectsAsync(Greeting);
        await Should.ThrowAsync<PostgresException>(() =>
            context.Database.SqlQueryRaw<string>("SELECT greeting({0}) AS \"Value\"", "x").SingleAsync(Token));

        await context.Database.CreateDatabaseObjectsAsync(Greeting);
        await context.Database.CreateDatabaseObjectsAsync(Greeting);
        (await context.Database.SqlQueryRaw<string>("SELECT greeting({0}) AS \"Value\"", "x").SingleAsync(Token)).ShouldBe("Hello, x");
    }
}
