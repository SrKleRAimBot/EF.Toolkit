using EFToolkit.Audit.Equivalence.Infrastructure;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace EFToolkit.Audit.Equivalence;

/// <summary>
///     That the equivalence harness reports divergence rather than quietly comparing nothing.
/// </summary>
/// <remarks>
///     Every assertion in <see cref="AuditEquivalenceTests" /> is worth exactly as much as this
///     file. A comparison that skipped the payload, sorted both sides into agreement, or read an
///     empty table twice would pass every scenario there and catch nothing — so each of those
///     failure modes gets a control that deliberately diverges and must be caught.
/// </remarks>
public abstract class HarnessSelfTests(AuditDatabaseFixture fixture)
{
    [Fact]
    public Task Detects_a_differing_number_of_entries()
        => ShouldDivergeAsync(
            async context =>
            {
                context.Products.AddRange(Products(5));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            },
            context => context.BulkInsertAsync(
                Products(4), cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public Task Detects_a_differing_payload()
        => ShouldDivergeAsync(
            async context =>
            {
                context.Products.AddRange(Products(3));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            },
            context => context.BulkInsertAsync(
                Products(3, name: "Different"),
                cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public Task Detects_a_differing_tenant()
        => ShouldDivergeAsync(
            async context =>
            {
                context.Products.AddRange(Products(3));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            },
            context => context.BulkInsertAsync(
                Products(3, tenant: "other"),
                cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public Task Detects_entries_that_were_never_written()
        => ShouldDivergeAsync(
            async context =>
            {
                context.Products.AddRange(Products(3));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            },
            context => context.BulkInsertAsync(
                Products(3),
                o => o.WithoutObservers(),
                TestContext.Current.CancellationToken));

    private async Task ShouldDivergeAsync(
        Func<ShopContext, Task> throughSaveChanges,
        Func<ShopContext, Task> throughBulk)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        var failure = await Should.ThrowAsync<XunitException>(
            () => AuditEquivalence.AssertAsync(fixture, throughSaveChanges, throughBulk));

        failure.Message.ShouldContain("diverge");
    }

    private static List<Product> Products(int count, string name = "Widget", string tenant = "acme")
        => [.. Enumerable.Range(1, count).Select(i => new Product
        {
            Sku = $"SKU-{i}",
            Name = name,
            Price = 9.99m,
            Status = ProductStatus.Draft,
            TenantId = tenant,
        })];
}
