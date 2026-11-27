using EFToolkit.Sql.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFToolkit.Sql.Integration;

[Trait("Engine", "mysql")]
[Collection("sql-mysql")]
public sealed class MySqlSessionContextTests(MySqlFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private AppDb Context(Action<DbContextOptionsBuilder>? configure = null)
    {
        var options = new DbContextOptionsBuilder<AppDb>()
            .UseMySQL(fixture.ConnectionString + ";Allow User Variables=True")
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        configure?.Invoke(options);
        return new AppDb(options.Options);
    }

    [Theory]
    [InlineData("ef_toolkit_value0")]
    [InlineData("EF_TOOLKIT_KEY0")]
    public async Task A_key_named_like_a_generated_parameter_is_set_and_read_back(string key)
    {
        // MySQL writes the key as the user variable @key beside the parameters, and the connector binds
        // every @name: a key named like the value's parameter had the value bound in its place — SET 7 = 7.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using var context = Context(o => o.UseSessionContext(s => s.Set(key, () => 7)));

        var sql = $"SELECT CAST(@{key} AS SIGNED) AS `Value`";
        (await context.Database.SqlQueryRaw<long>(sql).SingleAsync(Token)).ShouldBe(7);
    }

    [Fact]
    public async Task A_key_named_like_ef_s_own_raw_sql_parameter_is_set_and_read_back()
    {
        // SetSessionContextAsync went through raw SQL, whose first parameter EF names p0.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using var context = Context();
        await context.Database.OpenConnectionAsync(Token);
        try
        {
            await context.Database.SetSessionContextAsync("p0", 7, Token);

            (await context.Database.SqlQueryRaw<long>("SELECT CAST(@p0 AS SIGNED) AS `Value`").SingleAsync(Token)).ShouldBe(7);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
