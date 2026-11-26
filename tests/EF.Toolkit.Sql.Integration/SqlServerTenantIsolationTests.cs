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

    private async Task<int> CountAsync(string table, string key, object sessionValue)
    {
        await using var context = fixture.CreateContext(o => o.UseSessionContext(s => s.Set(key, () => sessionValue)));
        var sql = "SELECT COUNT(*) AS [Value] FROM " + table;
        return await context.Database.SqlQueryRaw<int>(sql).SingleAsync(Token);
    }

    [Fact]
    public async Task A_typed_value_that_only_prints_like_the_tenant_reaches_nothing()
    {
        // A float 1.00000001 rounds to the real 1, and both print as "1": the text fallback let it in.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        var policy = new TenantIsolationPolicy("Gauges", "Tenant", "TenantReal", "real", policyName: "gauges_iso");

        await using var admin = fixture.CreateContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Gauges (Id int PRIMARY KEY, Tenant real NOT NULL); INSERT INTO Gauges VALUES (1, 1);", Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(policy);

            (await CountAsync("Gauges", "TenantReal", 1.0)).ShouldBe(1);
            (await CountAsync("Gauges", "TenantReal", 1.00000001)).ShouldBe(0);
            (await CountAsync("Gauges", "TenantReal", "1")).ShouldBe(1);
        }
        finally
        {
            await admin.Database.DropDatabaseObjectsAsync(policy);
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Gauges", Token);
        }
    }

    [Fact]
    public async Task Tenants_a_binary_collated_column_keeps_apart_stay_apart_under_a_case_insensitive_database()
    {
        // The predicate's parameter compares under the database default collation, which here ignores
        // case, while the column tells "acme" from "ACME".
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        var policy = new TenantIsolationPolicy("Desks", "Tenant", "TenantCode", "varchar(10)", policyName: "desks_iso");

        await using var admin = fixture.CreateContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Desks (Id int PRIMARY KEY, Tenant varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL); "
                + "INSERT INTO Desks VALUES (1, 'acme'), (2, 'ACME');",
            Token);
        try
        {
            (await admin.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Desks WHERE Tenant = 'acme'").SingleAsync(Token)).ShouldBe(1);
            await admin.Database.CreateDatabaseObjectsAsync(policy);

            (await CountAsync("Desks", "TenantCode", "acme")).ShouldBe(1);
            (await CountAsync("Desks", "TenantCode", "ACME")).ShouldBe(1);
            (await CountAsync("Desks", "TenantCode", "Acme")).ShouldBe(0);
        }
        finally
        {
            await admin.Database.DropDatabaseObjectsAsync(policy);
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Desks", Token);
        }
    }

    [Theory]
    [InlineData("nvarchar(4)")]
    [InlineData("nvarchar(max)")]
    public async Task A_string_tenant_matches_only_its_exact_text_and_any_width_of_column_works(string storeType)
    {
        // nvarchar(4) truncated "acme " to "acme", and the padded sql_variant comparison let it read acme's
        // rows; nvarchar(max) could not be converted to sql_variant, so the policy failed to create.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        var policy = new TenantIsolationPolicy("Shelves", "Tenant", "TenantText", storeType, policyName: "shelves_iso");

        await using var admin = fixture.CreateContext();
        var create = "CREATE TABLE Shelves (Id int PRIMARY KEY, Tenant " + storeType + " NOT NULL); INSERT INTO Shelves VALUES (1, N'acme');";
        await admin.Database.ExecuteSqlRawAsync(create, Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(policy);

            (await CountAsync("Shelves", "TenantText", "acme")).ShouldBe(1);
            (await CountAsync("Shelves", "TenantText", "acme ")).ShouldBe(0);
            (await CountAsync("Shelves", "TenantText", "acm")).ShouldBe(0);
        }
        finally
        {
            await admin.Database.DropDatabaseObjectsAsync(policy);
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Shelves", Token);
        }
    }

    [Fact]
    public async Task A_UTF8_tenant_column_in_a_Latin1_database_keeps_its_characters()
    {
        // A varchar predicate parameter took the database's Latin-1 code page: 東京 arrived as ?? and the
        // tenant could no longer read its own rows.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        var policy = new TenantIsolationPolicy("Offices", "Tenant", "TenantCity", "varchar(20)", policyName: "offices_iso");

        await using var admin = fixture.CreateContext();
        (await admin.Database.SqlQueryRaw<string>("SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)) AS Value")
            .SingleAsync(Token)).ShouldNotContain("UTF8");
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Offices (Id int PRIMARY KEY, Tenant varchar(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL); "
                + "INSERT INTO Offices VALUES (1, N'東京'), (2, N'大阪');",
            Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(policy);

            (await CountAsync("Offices", "TenantCity", "東京")).ShouldBe(1);
            (await CountAsync("Offices", "TenantCity", "大阪")).ShouldBe(1);
            (await CountAsync("Offices", "TenantCity", "??")).ShouldBe(0);
        }
        finally
        {
            await admin.Database.DropDatabaseObjectsAsync(policy);
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Offices", Token);
        }
    }

    [Fact]
    public async Task Long_tenant_keys_that_differ_only_past_4000_characters_stay_apart()
    {
        // Cut to nvarchar(4000), 4000 a's and 4000 a's plus a b compared equal: each tenant read both rows.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        var policy = new TenantIsolationPolicy("Vaults8k", "Tenant", "TenantLong", "varchar(8000)", policyName: "vaults8k_iso");
        var shorter = new string('a', 4000);
        var longer = shorter + "b";

        await using var admin = fixture.CreateContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Vaults8k (Id int PRIMARY KEY, Tenant varchar(8000) NOT NULL); INSERT INTO Vaults8k VALUES (1, {0}), (2, {1});",
            [shorter, longer],
            Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(policy);

            // Only a varchar session value can be this long: sql_variant holds 8000 bytes, 4000 nvarchar characters.
            async Task<List<int>> IdsAs(string suffix)
            {
                await using var context = fixture.CreateContext();
                await context.Database.OpenConnectionAsync(Token);
                await context.Database.ExecuteSqlRawAsync(
                    "DECLARE @v varchar(8000) = REPLICATE(CAST('a' AS varchar(max)), 4000) + {0}; EXEC sp_set_session_context N'TenantLong', @v;",
                    [suffix],
                    Token);
                return await context.Database.SqlQueryRaw<int>("SELECT Id AS [Value] FROM Vaults8k").ToListAsync(Token);
            }

            (await IdsAs("")).ShouldBe([1]);
            (await IdsAs("b")).ShouldBe([2]);
        }
        finally
        {
            await admin.Database.DropDatabaseObjectsAsync(policy);
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Vaults8k", Token);
        }
    }
}
