using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Equivalence.Infrastructure;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFToolkit.Audit.Equivalence;

/// <summary>
///     That an audit entry and the change it describes succeed or fail together.
/// </summary>
/// <remarks>
///     The guarantee the default sink exists for. Its failure mode is the quiet one — a committed
///     change with no entry, or an entry for a change that was rolled back — so it is worth proving
///     in both directions against a real engine rather than reasoning about.
/// </remarks>
public abstract class AuditAtomicityTests(AuditDatabaseFixture fixture)
{
    [Fact]
    public async Task A_rolled_back_change_leaves_no_entry()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            context.Products.Add(NewProduct());
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountProductsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_sink_takes_the_change_with_it()
    {
        await ResetAsync();

        var services = new ServiceCollection()
            .AddSingleton<ThrowingAuditSink>()
            .BuildServiceProvider();

        await using (var context = fixture.CreateContext(
            a => a.WriteTo<ThrowingAuditSink>(),
            applicationServices: services))
        {
            context.Products.Add(NewProduct());

            await Should.ThrowAsync<InvalidOperationException>(
                () => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        }

        // OnAuditFailure(Throw) is the default, and it means exactly this: a change nobody could
        // record does not happen.
        (await CountProductsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_sink_can_be_tolerated_instead()
    {
        await ResetAsync();

        var services = new ServiceCollection()
            .AddSingleton<ThrowingAuditSink>()
            .BuildServiceProvider();

        await using (var context = fixture.CreateContext(
            a => a
                .WriteTo<ThrowingAuditSink>()
                .Atomicity(AuditAtomicity.BestEffort)
                .OnAuditFailure(AuditFailure.Ignore),
            applicationServices: services))
        {
            context.Products.Add(NewProduct());
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Only appropriate where the trail is genuinely advisory — and the failure is still
        // reported through AuditDiagnostics.SinkFailed.
        (await CountProductsAsync()).ShouldBe(1);
        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_context_writes_to_a_table_it_does_not_own()
    {
        await ResetAsync();

        // The modular-monolith arrangement: one context owns the audit schema's migrations, the
        // rest share the table and emit no DDL for it.
        await using (var owner = fixture.CreateContext())
        {
            owner.Products.Add(NewProduct("SKU-owner"));
            await owner.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var sharer = fixture.CreateContext(a => a.SharedAuditTables()))
        {
            sharer.Products.Add(NewProduct("SKU-sharer"));
            await sharer.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);

        entries.Count.ShouldBe(2);
        entries.Select(e => e.EntityKey).Distinct().Count().ShouldBe(2);
    }

    private Task ResetAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        return fixture.ResetAsync();
    }

    private async Task<int> CountProductsAsync()
    {
        await using var context = fixture.CreateContext(auditing: false);
        return await context.Products.CountAsync(TestContext.Current.CancellationToken);
    }

    private static Product NewProduct(string sku = "SKU-1") => new()
    {
        Sku = sku,
        Name = "Widget",
        Price = 9.99m,
        Status = ProductStatus.Draft,
        TenantId = "acme",
    };

    /// <summary>A sink that always fails, so the failure policy can be observed.</summary>
    private sealed class ThrowingAuditSink : IAuditSink
    {
        public Task WriteAsync(
            IReadOnlyList<AuditEntry> entries,
            AuditWriteContext context,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("The audit sink is deliberately broken.");
    }
}
