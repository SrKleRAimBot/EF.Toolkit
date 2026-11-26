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
