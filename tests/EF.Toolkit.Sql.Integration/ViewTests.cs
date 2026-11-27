using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EFToolkit.Sql.Integration;

[Trait("Engine", "postgres17")]
[Collection("sql-postgres17")]
public sealed class ViewTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly MaterializedViewDefinition OrderTotals = new(
        "order_totals",
        """SELECT "TenantId", sum("Total") AS "Total" FROM "Orders" GROUP BY "TenantId";""",
        withNoData: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var context = fixture.CreateAdminContext();
        context.Orders.AddRange(
            new Order { TenantId = 1, Total = 10m },
            new Order { TenantId = 1, Total = 200m },
            new Order { TenantId = 2, Total = 300m });
        await context.SaveChangesAsync(Token);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_view_is_created_replaced_and_dropped()
    {
        await using var context = fixture.CreateAdminContext();

        await context.Database.CreateDatabaseObjectsAsync(new ViewDefinition("big_orders", """SELECT "Id", "Total" FROM "Orders" WHERE "Total" > 100"""));
        (await context.BigOrders.CountAsync(Token)).ShouldBe(2);

        await context.Database.CreateDatabaseObjectsAsync(new ViewDefinition("big_orders", """SELECT "Id", "Total" FROM "Orders" WHERE "Total" > 250"""));
        (await context.BigOrders.Select(b => b.Total).SingleAsync(Token)).ShouldBe(300m);

        await context.Database.DropDatabaseObjectsAsync(new ViewDefinition("big_orders", "SELECT 1"));
        await Should.ThrowAsync<PostgresException>(() => context.BigOrders.CountAsync(Token));
    }

    [Fact]
    public async Task A_materialized_view_query_ending_in_a_comment_keeps_with_no_data_in_its_statement()
    {
        // The semicolon before the comment was left in: WITH NO DATA started a statement of its own.
        await using var context = fixture.CreateAdminContext();
        var noted = new MaterializedViewDefinition("noted_totals", "SELECT 1 AS id; -- initial query", withNoData: true);
        try
        {
            await context.Database.CreateDatabaseObjectsAsync(noted);

            var populated = "SELECT relispopulated AS \"Value\" FROM pg_class WHERE relname = 'noted_totals'";
            (await context.Database.SqlQueryRaw<bool>(populated).SingleAsync(Token)).ShouldBeFalse();
        }
        finally
        {
            await context.Database.DropDatabaseObjectsAsync(noted);
        }
    }

    [Fact]
    public async Task A_filter_after_a_comment_ended_by_a_carriage_return_stays_in_the_view()
    {
        // PostgreSQL ends a -- comment at a carriage return too: the trim took the line for one comment
        // and cut WHERE FALSE away, so the view returned a row.
        await using var context = fixture.CreateAdminContext();
        var view = new ViewDefinition("filtered_ids", "SELECT 1 AS \"Value\" -- note\rWHERE FALSE;");
        try
        {
            await context.Database.CreateDatabaseObjectsAsync(view);
            (await context.Database.SqlQueryRaw<int>("SELECT \"Value\" FROM filtered_ids").CountAsync(Token)).ShouldBe(0);
        }
        finally
        {
            await context.Database.DropDatabaseObjectsAsync(view);
        }
    }

    [Fact]
    public async Task An_escaped_quote_in_an_escape_string_keeps_its_value()
    {
        // E'a\';-- tail' is one string: the scan ended it at the escaped quote, dropped the semicolon
        // inside it, and the view returned a'-- tail.
        await using var context = fixture.CreateAdminContext();
        var view = new ViewDefinition("escaped_value", "SELECT E'a\\';-- tail' AS \"Value\"");
        try
        {
            await context.Database.CreateDatabaseObjectsAsync(view);
            (await context.Database.SqlQueryRaw<string>("SELECT \"Value\" FROM escaped_value").SingleAsync(Token)).ShouldBe("a';-- tail");
        }
        finally
        {
            await context.Database.DropDatabaseObjectsAsync(view);
        }
    }

    [Fact]
    public async Task A_no_break_space_ending_a_column_alias_stays_part_of_it()
    {
        // PostgreSQL takes U+00A0 into an unquoted identifier; Trim() took it off and renamed the column.
        await using var context = fixture.CreateAdminContext();
        var view = new ViewDefinition("spaced_alias", "SELECT 1 AS value\u00A0");
        try
        {
            await context.Database.CreateDatabaseObjectsAsync(view);
            var columns = "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'spaced_alias'";
            (await context.Database.SqlQueryRaw<string>(columns).SingleAsync(Token)).ShouldBe("value\u00A0");
        }
        finally
        {
            await context.Database.DropDatabaseObjectsAsync(view);
        }
    }

    [Fact]
    public async Task A_materialized_view_holds_its_snapshot_until_refreshed()
    {
        await using var context = fixture.CreateAdminContext();
        await context.Database.CreateDatabaseObjectsAsync(OrderTotals);

        // WITH NO DATA: unpopulated until the first refresh.
        // Some Npgsql versions class this error as transient, so EF's execution strategy may wrap it.
        var unpopulated = await Should.ThrowAsync<Exception>(() => TotalsAsync(context));
        var postgres = unpopulated as PostgresException ?? unpopulated.InnerException as PostgresException;
        postgres.ShouldNotBeNull(unpopulated.ToString()).SqlState.ShouldBe("55000");

        await context.Database.RefreshMaterializedViewAsync("order_totals", cancellationToken: Token);
        (await TotalsAsync(context)).ShouldBe([210m, 300m]);

        context.Orders.Add(new Order { TenantId = 2, Total = 1m });
        await context.SaveChangesAsync(Token);
        (await TotalsAsync(context)).ShouldBe([210m, 300m]);

        // CONCURRENTLY needs a unique index, which a recreate would drop along with the view.
        await PostgreSqlFixture.ExecuteAsync(fixture.AdminConnectionString, "CREATE UNIQUE INDEX ON order_totals (\"TenantId\")");
        await context.Database.RefreshMaterializedViewAsync("order_totals", concurrently: true, cancellationToken: Token);
        (await TotalsAsync(context)).ShouldBe([210m, 301m]);
    }

    [Fact]
    public async Task Creating_a_materialized_view_again_replaces_it()
    {
        await using var context = fixture.CreateAdminContext();

        await context.Database.CreateDatabaseObjectsAsync(new MaterializedViewDefinition("order_totals", "SELECT 1 AS \"Total\""));
        await context.Database.CreateDatabaseObjectsAsync(new MaterializedViewDefinition("order_totals", "SELECT 2 AS \"Total\""));

        (await context.Database.SqlQueryRaw<int>("SELECT \"Total\" AS \"Value\" FROM order_totals").SingleAsync(Token)).ShouldBe(2);

        await context.Database.DropDatabaseObjectsAsync(OrderTotals);
        await Should.ThrowAsync<PostgresException>(() => TotalsAsync(context));
    }

    private static Task<List<decimal>> TotalsAsync(AppDb context)
        => context.Database
            .SqlQueryRaw<decimal>("SELECT \"Total\" AS \"Value\" FROM order_totals ORDER BY \"TenantId\"")
            .ToListAsync(Token);
}
