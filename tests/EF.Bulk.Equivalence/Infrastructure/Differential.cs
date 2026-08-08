using EFBulk.Equivalence.Model;

namespace EFBulk.Equivalence.Infrastructure;

/// <summary>
///     Runs a scenario twice — once through stock EF Core, once through EF.Bulk — and asserts the
///     two runs are indistinguishable.
/// </summary>
/// <remarks>
///     This is the project's primary correctness gate. Bulk acceleration is only ever an
///     optimisation, so the specification of "correct" is simply "whatever stock EF did", and that
///     is cheaper and far more reliable to assert differentially than to restate as expectations in
///     every test.
/// </remarks>
public static class Differential
{
    /// <summary>
    ///     Asserts that <paramref name="scenario" /> produces identical database contents, change
    ///     tracker state, and failure behaviour under stock EF Core and under EF.Bulk.
    /// </summary>
    /// <param name="fixture">Supplies the two databases.</param>
    /// <param name="scenario">The work to perform. Runs once against each database.</param>
    public static async Task AssertAsync(DatabaseFixture fixture, Func<ShopContext, Task> scenario)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        await fixture.ResetAsync();

        var stock = await RunAsync(fixture.CreateStockContext, scenario);
        var bulk = await RunAsync(fixture.CreateBulkContext, scenario);

        // Every divergence is reported together rather than failing on the first. A wrong bulk
        // path usually breaks the stored rows and the tracker at once, and seeing only the first
        // symptom sends you looking in the wrong place.
        var failures = new List<string>();

        if (DescribeExceptionMismatch(stock.Exception, bulk.Exception) is { } exceptionDiff)
        {
            failures.Add(exceptionDiff);
        }

        if (stock.Tables.Diff(bulk.Tables) is { } tableDiff)
        {
            failures.Add("Database contents diverged after the scenario." + Environment.NewLine + tableDiff);
        }

        if (stock.Tracker.Diff(bulk.Tracker) is { } trackerDiff)
        {
            failures.Add("Change tracker diverged after the scenario." + Environment.NewLine + trackerDiff);
        }

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine + Environment.NewLine, failures));
        }
    }

    private sealed record Run(Exception? Exception, TrackerSnapshot Tracker, TableSnapshot Tables);

    private static async Task<Run> RunAsync(
        Func<ShopContext> createContext,
        Func<ShopContext, Task> scenario)
    {
        await using var context = createContext();

        Exception? exception = null;
        try
        {
            await scenario(context);
        }
        catch (Exception ex)
        {
            // The scenario failing is itself a comparable outcome: stock EF and EF.Bulk must fail
            // the same way, not merely succeed the same way.
            exception = ex;
        }

        var tracker = TrackerSnapshot.Capture(context);
        var tables = await TableSnapshot.CaptureAsync(context);

        return new Run(exception, tracker, tables);
    }

    private static string? DescribeExceptionMismatch(Exception? stock, Exception? bulk)
        => (stock, bulk) switch
        {
            (null, null) => null,

            (null, not null) =>
                "Scenario succeeded under stock EF but threw under EF.Bulk:"
                + Environment.NewLine + bulk,

            (not null, null) =>
                "Scenario threw under stock EF but succeeded under EF.Bulk:"
                + Environment.NewLine + stock,

            _ when stock!.GetType() != bulk!.GetType() =>
                $"Stock EF threw {stock.GetType().Name} but EF.Bulk threw {bulk.GetType().Name}."
                + Environment.NewLine + "stock: " + stock
                + Environment.NewLine + "bulk : " + bulk,

            _ => null
        };
}
