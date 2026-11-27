using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Sql.Integration;

[Trait("Engine", "sqlserver")]
[Collection("sql-sqlserver")]
public sealed class SqlServerObjectTests(SqlServerFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_objects_inside_a_transaction_leaves_the_caller_s_own_savepoint_in_charge()
    {
        // SQL Server has no savepoint release: a second savepoint of the same name stays, and rolling back
        // to the name goes to the latest. The helper's fixed name could shadow the caller's own, and the
        // caller's rollback then kept the row it meant to undo.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE Notes (Id int PRIMARY KEY)", Token);
        try
        {
            await using (var transaction = await context.Database.BeginTransactionAsync(Token))
            {
                await transaction.CreateSavepointAsync("ef_toolkit_objects", Token);
                await context.Database.ExecuteSqlRawAsync("INSERT INTO Notes VALUES (1)", Token);
                await context.Database.CreateDatabaseObjectsAsync(
                    [new ViewDefinition("NoteIds", "SELECT Id FROM Notes")], Token);

                await transaction.RollbackToSavepointAsync("ef_toolkit_objects", Token);
                await transaction.CommitAsync(Token);
            }

            (await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Notes").SingleAsync(Token)).ShouldBe(0);
            (await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.views WHERE name = 'NoteIds'").SingleAsync(Token))
                .ShouldBe(0);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP VIEW IF EXISTS NoteIds; DROP TABLE IF EXISTS Notes", Token);
        }
    }
}
