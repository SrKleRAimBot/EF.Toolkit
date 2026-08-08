using EFBulk.Equivalence.Infrastructure;
using EFBulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFBulk.Equivalence;

/// <summary>
///     Negative controls for the differential harness itself.
/// </summary>
/// <remarks>
///     Every scenario in the suite currently passes, which proves nothing on its own: until the
///     bulk execution paths land, EF.Bulk delegates everything to the provider, so the two sides
///     are identical by construction. These tests deliberately diverge the two databases and assert
///     the harness notices. Without them, a harness that silently compared nothing would look
///     exactly like a harness that was passing.
/// </remarks>
public abstract class HarnessSelfTests(DatabaseFixture fixture)
{
    /// <summary>Writes different data depending on which of the two databases it is given.</summary>
    private static bool IsBulkSide(ShopContext context)
        => context.Database.GetConnectionString()!.Contains("efbulk_bulk", StringComparison.Ordinal);

    [Fact]
    public async Task Detects_differing_row_counts()
    {
        var failure = await CaptureFailure(async context =>
        {
            context.Customers.AddRange(Customers(IsBulkSide(context) ? 4 : 5));
            await context.SaveChangesAsync();
        });

        failure.ShouldContain("Database contents diverged");
        failure.ShouldContain("5 row(s)");
        failure.ShouldContain("Change tracker diverged");
    }

    [Fact]
    public async Task Detects_differing_column_values()
    {
        var failure = await CaptureFailure(async context =>
        {
            var customers = Customers(5);
            customers[2].Name = IsBulkSide(context) ? "diverged" : "expected";
            context.Customers.AddRange(customers);
            await context.SaveChangesAsync();
        });

        failure.ShouldContain("Database contents diverged");
        failure.ShouldContain("Name");
    }

    [Fact]
    public async Task Detects_differing_change_tracker_state()
    {
        var failure = await CaptureFailure(async context =>
        {
            var customers = Customers(5);
            context.Customers.AddRange(customers);
            await context.SaveChangesAsync();

            // Same rows on disk, different tracker state. This is the class of bug that a
            // database-only comparison would sail straight past.
            if (IsBulkSide(context))
            {
                context.Entry(customers[0]).State = EntityState.Detached;
            }
        });

        failure.ShouldContain("Change tracker diverged");
    }

    [Fact]
    public async Task Detects_a_failure_on_only_one_side()
    {
        var failure = await CaptureFailure(async context =>
        {
            var customers = Customers(5);
            if (IsBulkSide(context))
            {
                customers[^1].Email = customers[0].Email; // violates the unique index
            }

            context.Customers.AddRange(customers);
            await context.SaveChangesAsync();
        });

        failure.ShouldContain("succeeded under stock EF but threw under EF.Bulk");
    }

    private async Task<string> CaptureFailure(Func<ShopContext, Task> divergentScenario)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        var exception = await Record.ExceptionAsync(
            () => Differential.AssertAsync(fixture, divergentScenario));

        exception.ShouldNotBeNull("The harness did not detect a deliberate divergence.");
        return exception.Message;
    }

    private static List<Customer> Customers(int count)
        =>
        [
            .. Enumerable.Range(0, count).Select(i => new Customer
            {
                Name = $"Customer {i}",
                Email = $"customer{i}@example.com",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i)
            })
        ];
}
