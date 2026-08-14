using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Equivalence.Infrastructure;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Equivalence;

/// <summary>
///     That the payload is queryable, and that the index meant to make it so is used.
/// </summary>
/// <remarks>
///     The reason the payload is <c>jsonb</c> rather than text, and the reason its shape puts
///     <c>old</c> and <c>new</c> side by side rather than pairing them per property. Without these
///     assertions the JSON requirement is only a claim about a column type: the trail could be
///     perfectly well formed and still answer no question without a sequential scan.
/// </remarks>
public abstract class PayloadQueryTests(PostgreSqlAuditFixture fixture)
{
    [Fact]
    public async Task Containment_finds_entries_by_what_changed_to()
    {
        await SeedChangesAsync();

        var count = await ScalarAsync(
            $"SELECT count(*) FROM {Table()} WHERE {Changes()} @> '{{\"new\":{{\"Status\":\"Live\"}}}}'");

        count.ShouldBe(3);
    }

    [Fact]
    public async Task Containment_finds_entries_by_what_changed_from()
    {
        await SeedChangesAsync();

        var count = await ScalarAsync(
            $"SELECT count(*) FROM {Table()} WHERE {Changes()} @> '{{\"old\":{{\"Status\":\"Draft\"}}}}'");

        count.ShouldBe(3);
    }

    [Fact]
    public async Task The_changed_list_answers_which_column_moved()
    {
        await SeedChangesAsync();

        var count = await ScalarAsync(
            $"SELECT count(*) FROM {Table()} WHERE {Changes()} -> 'changed' ? 'Status'");

        count.ShouldBe(3);
    }

    [Fact]
    public async Task The_payload_index_is_a_gin_index_over_jsonb()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        // A B-tree index on a jsonb column answers equality over the whole document and nothing
        // else, which is not a question anybody asks of an audit payload. Asserting the method is
        // what keeps the column type and the index honest about each other.
        var method = await TextAsync(
            """
            SELECT am.amname
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            JOIN pg_am am ON am.oid = c.relam
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(i.indkey)
            WHERE n.nspname = 'audit' AND a.attname = 'Changes'
            """);

        method.ShouldBe("gin");

        var type = await TextAsync(
            """
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = 'audit' AND column_name = 'Changes'
            """);

        type.ShouldBe("jsonb");
    }

    private async Task SeedChangesAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        await using var context = fixture.CreateContext();

        context.Products.AddRange(
            Enumerable.Range(1, 5).Select(i => new Product
            {
                Sku = $"SKU-{i}",
                Name = "Widget",
                Price = 9.99m,
                Status = ProductStatus.Draft,
                TenantId = "acme",
            }));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        foreach (var product in await context.Products.Take(3)
                     .ToListAsync(TestContext.Current.CancellationToken))
        {
            product.Status = ProductStatus.Live;
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private string Table()
        => $"{fixture.Quote(AuditOptions.DefaultSchema)}.{fixture.Quote(AuditOptions.DefaultTableName)}";

    private string Changes() => fixture.Quote("Changes");

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = fixture.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string?> TextAsync(string sql)
    {
        await using var connection = fixture.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return (await command.ExecuteScalarAsync()) as string;
    }
}
