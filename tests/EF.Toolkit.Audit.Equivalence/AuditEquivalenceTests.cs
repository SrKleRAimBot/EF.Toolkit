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
                    product.Price = 12.5m;
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
                    product.Price = 12.5m;
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
    ///     <para>
    ///         The decimals here are deliberately at scales their columns do not declare. A
    ///         <see cref="decimal" /> carries its scale as part of its representation, and the two
    ///         paths obtain their values from different places: the change tracker holds what the
    ///         application assigned, while a bulk operation reads a deleted row's before-image back
    ///         from the store, where <c>numeric(18,2)</c> has been applied. So <c>20m</c> arrives as
    ///         <c>20</c> one way and <c>20.00</c> the other, and the entries diverge over a
    ///         difference that carries no information.
    ///     </para>
    ///     <para>
    ///         Seeding scale-matched values would make these tests pass without saying anything.
    ///         Every decimal below is mismatched on purpose — scale 0 and scale 1 into a scale-2
    ///         column — so the payload writer's canonicalization is what keeps them equal.
    ///     </para>
    /// </remarks>
    private static List<Product> Products(int count)
        => [.. Enumerable.Range(1, count).Select(i => new Product
        {
            Sku = $"SKU-{i}",
            Name = "Widget",
            Price = 9.9m,
            Status = ProductStatus.Draft,
            TenantId = "acme",
            Dimensions = new Dimensions
            {
                Width = 10.5m,
                Height = 20m,
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
