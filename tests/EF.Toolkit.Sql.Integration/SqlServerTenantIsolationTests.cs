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

    [Fact]
    public async Task A_typed_decimal_tenant_accepts_added_scale_but_not_rounding()
    {
        // decimal(10,2) renders 12.5 back as 12.50, so the text check alone refused the very tenant.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        var policy = new TenantIsolationPolicy("Ledgers", "Tenant", "TenantNum", "decimal(10,2)", policyName: "ledgers_iso");

        await using var admin = fixture.CreateContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Ledgers (Id int PRIMARY KEY, Tenant decimal(10,2) NOT NULL); INSERT INTO Ledgers VALUES (1, 12.50);", Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(policy);

            async Task<int> CountAs(object sessionValue)
            {
                await using var context = fixture.CreateContext(o => o.UseSessionContext(s => s.Set("TenantNum", () => sessionValue)));
                return await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Ledgers").SingleAsync(Token);
            }

            (await CountAs(12.5m)).ShouldBe(1);
            (await CountAs(12.50m)).ShouldBe(1);
            (await CountAs(12.555m)).ShouldBe(0);
            (await CountAs("12.50")).ShouldBe(1);
            (await CountAs("12.555")).ShouldBe(0);
        }
        finally
        {
            await admin.Database.DropDatabaseObjectsAsync(policy);
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Ledgers", Token);
        }
    }

    [Fact]
    public async Task A_typed_datetime2_tenant_matches_its_instant_but_not_a_rounded_one()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        var tenant = new DateTime(2026, 11, 26, 12, 30, 0);
        var policy = new TenantIsolationPolicy("Cohorts", "Tenant", "TenantAt", "datetime2(0)", policyName: "cohorts_iso");

        await using var admin = fixture.CreateContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Cohorts (Id int PRIMARY KEY, Tenant datetime2(0) NOT NULL); INSERT INTO Cohorts VALUES (1, '2026-11-26T12:30:00');", Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(policy);

            async Task<int> CountAs(object sessionValue)
            {
                await using var context = fixture.CreateContext(o => o.UseSessionContext(s => s.Set("TenantAt", () => sessionValue)));
                return await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Cohorts").SingleAsync(Token);
            }

            (await CountAs(tenant)).ShouldBe(1);
            (await CountAs(tenant.AddMilliseconds(400))).ShouldBe(0);
        }
        finally
        {
            await admin.Database.DropDatabaseObjectsAsync(policy);
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Cohorts", Token);
        }
    }
}
