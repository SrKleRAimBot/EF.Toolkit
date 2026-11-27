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

    private sealed class FailingObject : IDatabaseObject
    {
        public IReadOnlyList<string> GetCreateSql(SqlDialect dialect)
            => ["CREATE VIEW Doomed AS SELECT 1 AS One", "THROW 50001, 'the object refuses', 1;"];

        public IReadOnlyList<string> GetDropSql(SqlDialect dialect) => ["DROP VIEW IF EXISTS Doomed"];
    }

    [Fact]
    public async Task A_statement_error_that_dooms_the_transaction_is_the_error_reported()
    {
        // Under XACT_ABORT the THROW dooms the caller's transaction, and rolling back to the savepoint
        // fails too: that second error replaced the statement's, which was the one that explained it.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using var context = fixture.CreateContext();
        await context.Database.OpenConnectionAsync(Token);
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON", Token);
            await using var transaction = await context.Database.BeginTransactionAsync(Token);

            var error = await Should.ThrowAsync<AggregateException>(
                () => context.Database.CreateDatabaseObjectsAsync([new FailingObject()], Token));

            error.InnerExceptions[0].ShouldBeOfType<Microsoft.Data.SqlClient.SqlException>().Number.ShouldBe(50001);
            await transaction.RollbackAsync(Token);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("SET XACT_ABORT OFF; DROP VIEW IF EXISTS Doomed", Token);
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task An_escaped_bracket_in_a_column_alias_keeps_the_semicolon_after_it()
    {
        // ]] inside [ ] is an escaped ], not the end: the scan ended the alias there, read --tail] as a
        // comment, and took the semicolon out of the column's name.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using var context = fixture.CreateContext();
        var view = new ViewDefinition("BracketAlias", "SELECT 1 AS [a]]b;--tail];");
        try
        {
            await context.Database.CreateDatabaseObjectsAsync([view], Token);

            var columns = "SELECT name AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('BracketAlias')";
            (await context.Database.SqlQueryRaw<string>(columns).SingleAsync(Token)).ShouldBe("a]b;--tail");
        }
        finally
        {
            await context.Database.DropDatabaseObjectsAsync([view], Token);
        }
    }
}
