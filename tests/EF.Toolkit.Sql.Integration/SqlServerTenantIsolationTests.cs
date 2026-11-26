using EFToolkit.Sql.Integration.Infrastructure;
using EFToolkit.Sql.Security;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Sql.Integration;

[Trait("Engine", "sqlserver")]
[Collection("sql-sqlserver")]
public sealed class SqlServerTenantIsolationTests(SqlServerFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_tenant_GUID_with_trailing_text_reaches_nothing_while_the_typed_GUID_reaches_its_rows()
    {
        // A string holding a GUID followed by more text converts to that GUID, and SQL Server's string
        // equality ignores trailing spaces — so "{guid} " used to pass both halves of the check.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        var tenant = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        var policy = new TenantIsolationPolicy("Vaults", "Tenant", "TenantKey", "uniqueidentifier", policyName: "vaults_iso");

        await using var admin = fixture.CreateContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Vaults (Id int PRIMARY KEY, Tenant uniqueidentifier NOT NULL); INSERT INTO Vaults VALUES (1, {0});",
            [tenant],
            Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(policy);

            async Task<int> CountAs(object sessionValue)
            {
                await using var context = fixture.CreateContext(o => o.UseSessionContext(s => s.Set("TenantKey", () => sessionValue)));
                return await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Vaults").SingleAsync(Token);
            }

            (await CountAs(tenant)).ShouldBe(1);
            (await CountAs(tenant + " ")).ShouldBe(0);
            (await CountAs(tenant + "-and-more")).ShouldBe(0);
        }
        finally
        {
            await admin.Database.DropDatabaseObjectsAsync(policy);
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Vaults", Token);
        }
    }
}
