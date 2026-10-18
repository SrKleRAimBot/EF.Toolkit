using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.MySql;
using EFToolkit.Audit.Sqlite;
using EFToolkit.Audit.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Shouldly;

namespace EFToolkit.Audit.Tests.Configuration;

/// <summary>
///     The audit table the MySQL and SQLite provider packages describe.
/// </summary>
public class ProviderStoreTypeTests
{
    [Fact]
    public void MySql_stores_the_payload_as_native_json_with_microsecond_timestamps()
    {
        var types = MySqlAuditOptionsBuilder.StoreTypes;

        types.Json.ShouldBe("json");
        types.Timestamp.ShouldBe("datetime(6)");
        types.Text.ShouldBe("varchar(256)");

        // MySQL's json type validates on the way in, so a check would only cost a second parse.
        types.JsonCheck.ShouldBeNull();
    }

    [Fact]
    public void MySql_keeps_the_table_in_the_contexts_own_database_unless_told_otherwise()
    {
        new MySqlAuditOptionsBuilder(AuditOptions.Default).Options.Schema.ShouldBeNull();

        var configured = new MySqlAuditOptionsBuilder(AuditOptions.Default);
        configured.Schema("history");
        configured.Options.Schema.ShouldBe("history");
    }

    [Fact]
    public void MySql_indexes_a_json_path_through_an_unquoted_generated_column()
    {
        var builder = new MySqlAuditOptionsBuilder(AuditOptions.Default);
        builder.IndexJsonPath("$.new.O'Brien");

        var index = builder.Options.StoreTypes.JsonPathIndexes.ShouldHaveSingleItem();

        index.Name.ShouldBe("Json_newOBrien");
        index.StoreType.ShouldBe("varchar(256)");
        index.ComputedSql.ShouldBe("JSON_UNQUOTE(JSON_EXTRACT(`Changes`, '$.new.O''Brien'))");
    }

    [Fact]
    public void MySql_maps_the_audit_table_onto_its_store_types()
    {
        using var context = Context(o => o
            .UseMySQL("Server=localhost;Database=efaudit")
            .UseMySqlAuditing());

        var entry = AuditEntryType(context);

        entry.GetSchema().ShouldBeNull();
        entry.FindProperty(nameof(AuditEntry.Changes))!.GetColumnType().ShouldBe("json");
        entry.FindProperty(nameof(AuditEntry.OccurredAt))!.GetColumnType().ShouldBe("datetime(6)");
        entry.FindProperty(nameof(AuditEntry.EntityKey))!.GetColumnType().ShouldBe("varchar(256)");
    }

    [Fact]
    public void Sqlite_guards_the_payload_with_json_valid()
    {
        var types = SqliteAuditOptionsBuilder.StoreTypes;

        types.Json.ShouldBe("TEXT");
        types.JsonCheck.ShouldBe("json_valid(\"{0}\")");
        new SqliteAuditOptionsBuilder(AuditOptions.Default).Options.Schema.ShouldBeNull();
    }

    [Fact]
    public async Task Sqlite_creates_a_table_that_refuses_a_payload_that_is_not_json()
    {
        await using var connection = await CreatedAsync(_ => { });
        await using var insert = Insert(connection, "not json");

        var refused = await Should.ThrowAsync<SqliteException>(
            () => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        refused.Message.ShouldContain("CHECK constraint failed");
    }

    [Fact]
    public async Task Sqlite_indexes_a_json_path_through_a_generated_column()
    {
        await using var connection = await CreatedAsync(a => a.IndexJsonPath("$.new.Status"));
        await using var insert = Insert(connection, """{"new":{"Status":"Shipped"}}""");

        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // The generated column holds the extracted value, which is what makes it indexable at all.
        await using var read = connection.CreateCommand();
        read.CommandText = """SELECT "Json_newStatus" FROM "AuditEntries" """;

        (await read.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("Shipped");
    }

    /// <summary>An in-memory database holding the audit table the SQLite package describes.</summary>
    private static async Task<SqliteConnection> CreatedAsync(Action<SqliteAuditOptionsBuilder> configure)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var context = Context(o => o.UseSqlite(connection).UseSqliteAuditing(configure));
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        return connection;
    }

    private static SqliteCommand Insert(SqliteConnection connection, string changes)
    {
        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO "AuditEntries"
                ("Id", "EntityType", "EntityKey", "Source", "Operation", "OccurredAt", "Changes")
            VALUES (@id, 'Order', '1', 'test', 1, '2026-01-01 00:00:00+00:00', @changes)
            """;

        insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString().ToUpperInvariant());
        insert.Parameters.AddWithValue("@changes", changes);
        return insert;
    }

    private static Microsoft.EntityFrameworkCore.Metadata.IEntityType AuditEntryType(DbContext context)
        => context.Model.GetEntityTypes().Single(
            e => e.ClrType.IsGenericType && e.ClrType.GetGenericTypeDefinition() == typeof(AuditEntry<>));

    private static AuditTestContext Context(Action<DbContextOptionsBuilder<AuditTestContext>> configure)
    {
        var builder = new DbContextOptionsBuilder<AuditTestContext>();
        configure(builder);
        builder.ReplaceService<IModelCacheKeyFactory, PerInstanceModelCacheKeyFactory>();

        return new AuditTestContext(builder.Options);
    }
}
