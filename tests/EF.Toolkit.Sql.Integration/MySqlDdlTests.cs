using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Sql.Integration;

/// <summary>MySQL commits implicitly before every DDL statement; the runtime helpers must not hide that.</summary>
[Trait("Engine", "mysql")]
[Collection("sql-mysql")]
public sealed class MySqlDdlTests(MySqlFixture fixture)
{
    private static readonly ViewDefinition BigOrders = new("big_orders", "SELECT Id, Total FROM Orders WHERE Total > 100");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("SELECT 3--2 AS Value;", 1, 5)]
    [InlineData("SELECT 1 AS Value /*! FROM DUAL WHERE FALSE */;", 0, 0)]
    public async Task A_view_keeps_what_MySQL_runs_that_looks_like_a_comment(string query, int rows, int value)
    {
        // 3--2 is 3 - -2 on MySQL, and /*! … */ runs: the trim took both for comments and cut them away.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using var context = fixture.CreateContext();
        var view = new ViewDefinition("looks_commented", query);
        try
        {
            await context.Database.CreateDatabaseObjectsAsync(view);

            var values = await context.Database.SqlQueryRaw<long>("SELECT Value FROM looks_commented").ToListAsync(Token);
            values.Count.ShouldBe(rows);
            if (rows > 0)
            {
                values[0].ShouldBe(value);
            }
        }
        finally
        {
            await context.Database.DropDatabaseObjectsAsync(view);
        }
    }

    [Fact]
    public async Task Inside_a_transaction_creating_objects_is_refused_before_any_DDL_so_earlier_work_can_still_roll_back()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM Orders", Token);

        await using (var transaction = await context.Database.BeginTransactionAsync(Token))
        {
            context.Orders.Add(new Order { TenantId = 1, Total = 500 });
            await context.SaveChangesAsync(Token);

            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.CreateDatabaseObjectsAsync(BigOrders)))
                .Message.ShouldContain("commits implicitly");

            await transaction.RollbackAsync(Token);
        }

        (await context.Orders.CountAsync(Token)).ShouldBe(0);
        (await context.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM information_schema.VIEWS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'big_orders'")
            .SingleAsync(Token)).ShouldBe(0);
    }

    [Fact]
    public async Task A_procedure_with_a_reserved_parameter_name_is_created_replaced_and_called()
    {
        // `select` cannot be a bare parameter name; written bare, MySQL refuses the CREATE — after the
        // replacement's DROP has already removed the procedure that was there.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        var procedure = new StoredProcedureDefinition(
            "orders_select",
            [new("select", "int")],
            "BEGIN INSERT INTO Orders (TenantId, Total, Note) VALUES (9, `select`, 'reserved'); END");

        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM Orders", Token);
        await context.Database.CreateDatabaseObjectsAsync(procedure);
        try
        {
            await context.Database.CreateDatabaseObjectsAsync(procedure);
            await context.Database.ExecuteProcedureAsync("orders_select", new { Select = 77 }, cancellationToken: Token);

            (await context.Orders.SingleAsync(o => o.Note == "reserved", Token)).Total.ShouldBe(77m);
        }
        finally
        {
            await context.Database.DropDatabaseObjectsAsync(procedure);
        }
    }

    [Fact]
    public async Task Outside_a_transaction_objects_are_created_and_dropped()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM Orders", Token);
        context.Orders.AddRange(new Order { TenantId = 1, Total = 500 }, new Order { TenantId = 1, Total = 5 });
        await context.SaveChangesAsync(Token);

        await context.Database.CreateDatabaseObjectsAsync(BigOrders);
        try
        {
            (await context.BigOrders.CountAsync(Token)).ShouldBe(1);
        }
        finally
        {
            await context.Database.DropDatabaseObjectsAsync(BigOrders);
        }

        await Should.ThrowAsync<Exception>(() => context.BigOrders.CountAsync(Token));
    }
}
