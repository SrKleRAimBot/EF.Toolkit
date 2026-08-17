using EFToolkit.Audit.Equivalence.Infrastructure;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Equivalence;

/// <summary>
///     The same change, written two ways, audited identically.
/// </summary>
/// <remarks>
///     The claim under test is the one the whole design rests on: an application that reaches for
///     the explicit bulk API does not thereby get a weaker audit trail. Since the two paths share
///     nothing but the entry factory, this is also the test that would catch either of them growing
///     an opinion of its own.
/// </remarks>
public abstract class AuditEquivalenceTests(AuditDatabaseFixture fixture)
{
    [Fact]
    public Task Insert_is_audited_the_same_either_way()
        => AuditEquivalence.AssertAsync(
            fixture,
            async context =>
            {
                context.Products.AddRange(Products(25));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            },
            context => context.BulkInsertAsync(
                Products(25), cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public Task Update_is_audited_the_same_either_way()
        => AuditEquivalence.AssertAsync(
            fixture,
            async context =>
            {
                context.Products.AddRange(Products(15));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                await ClearAuditAsync();

                foreach (var product in await context.Products
                             .ToListAsync(TestContext.Current.CancellationToken))
                {
                    product.Name = "Renamed";
                    product.Price = 12.50m;
                }

                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            },
            async context =>
            {
                var products = Products(15);
                context.Products.AddRange(products);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                await ClearAuditAsync();

                foreach (var product in products)
                {
                    product.Name = "Renamed";
                    product.Price = 12.50m;
                }

                context.ChangeTracker.Clear();

                await context.BulkUpdateAsync(
                    products, cancellationToken: TestContext.Current.CancellationToken);
            });

    [Fact]
    public Task Delete_is_audited_the_same_either_way()
        => AuditEquivalence.AssertAsync(
            fixture,
            async context =>
            {
                context.Products.AddRange(Products(12));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                await ClearAuditAsync();

                context.Products.RemoveRange(
                    await context.Products.ToListAsync(TestContext.Current.CancellationToken));

                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            },
            async context =>
            {
                var products = Products(12);
                context.Products.AddRange(products);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                await ClearAuditAsync();

                context.ChangeTracker.Clear();

                await context.BulkDeleteAsync(
                    products, cancellationToken: TestContext.Current.CancellationToken);
            });

    [Fact]
    public Task Masking_and_exclusion_hold_on_both_paths()
        => AuditEquivalence.AssertAsync(
            fixture,
            async context =>
            {
                context.Products.AddRange(Sensitive(8));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            },
            context => context.BulkInsertAsync(
                Sensitive(8), cancellationToken: TestContext.Current.CancellationToken));

    private async Task ClearAuditAsync()
    {
        var table = $"{fixture.Quote(Audit.Configuration.AuditOptions.DefaultSchema)}."
            + $"{fixture.Quote(Audit.Configuration.AuditOptions.DefaultTableName)}";

        await using var connection = fixture.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {table}";
        await command.ExecuteNonQueryAsync();
    }

    /// <remarks>
    ///     Decimals are written at the scale their column declares. The change-tracker path records
    ///     the value the application supplied and the bulk path reads a deleted row's back from the
    ///     store, so a decimal whose CLR scale differs from its column's — <c>1.5m</c> into a
    ///     <c>numeric(18,2)</c> — is rendered <c>1.5</c> by one and <c>1.50</c> by the other. That
    ///     is a pre-existing difference in how the two paths obtain a before-image and has nothing
    ///     to do with what these tests cover, so the seed does not walk into it.
    /// </remarks>
    private static List<Product> Products(int count)
        => [.. Enumerable.Range(1, count).Select(i => new Product
        {
            Sku = $"SKU-{i}",
            Name = "Widget",
            Price = 9.99m,
            Status = ProductStatus.Draft,
            TenantId = "acme",
            Dimensions = new Dimensions
            {
                Width = 10.50m,
                Height = 20.25m,
                Packaging = new Packaging { Material = "card" },
            },
        })];

    private static List<Product> Sensitive(int count)
        => [.. Products(count).Select(p =>
        {
            p.CardNumber = "4111111111111111";
            p.InternalNotes = "do not ship";
            return p;
        })];
}
