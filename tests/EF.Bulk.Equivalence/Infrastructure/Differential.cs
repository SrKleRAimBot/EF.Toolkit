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
    /// <param name="failureMessagesDifferBecause">
    ///     Why the two sides' failure messages are expected to read differently, for the rare
    ///     scenario where they legitimately do. Supplying it relaxes only the message comparison —
    ///     the exception types, at every level of nesting, must still match. Leave it null, which
    ///     is the norm: a differing message is usually a real divergence in failure behaviour.
    /// </param>
    public static async Task AssertAsync(
        DatabaseFixture fixture,
        Func<ShopContext, Task> scenario,
        string? failureMessagesDifferBecause = null)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        await fixture.ResetAsync();

        var stock = await RunAsync(() => fixture.CreateStockContext(), scenario);
        var bulk = await RunAsync(() => fixture.CreateBulkContext(), scenario);

        // Every divergence is reported together rather than failing on the first. A wrong bulk
        // path usually breaks the stored rows and the tracker at once, and seeing only the first
        // symptom sends you looking in the wrong place.
        var failures = new List<string>();

        if (DescribeExceptionMismatch(stock.Exception, bulk.Exception, failureMessagesDifferBecause)
            is { } exceptionDiff)
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

    /// <summary>
    ///     Describes how the two sides' failures differ, or <see langword="null" /> if they match.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Matching on the exception type alone is too weak: a bulk path that reported the
    ///         wrong constraint, the wrong parameter or the wrong row count would still throw the
    ///         same type as stock EF, and applications routinely branch on what the message says.
    ///         So the whole nesting chain of types is compared, and so is the message an
    ///         application actually sees.
    ///     </para>
    ///     <para>
    ///         Messages of <em>inner</em> exceptions are deliberately not compared. Those are the
    ///         store's own words about a statement, and the statement genuinely differs: stock EF
    ///         violates a constraint with an <c>INSERT</c>, EF.Bulk with a bulk copy or a staged
    ///         set-based statement, and the same fault is worded differently by both engines. The
    ///         type of the inner exception is the part that is contractual, and that is compared.
    ///     </para>
    /// </remarks>
    private static string? DescribeExceptionMismatch(
        Exception? stock,
        Exception? bulk,
        string? messagesMayDiffer)
    {
        switch (stock, bulk)
        {
            case (null, null):
                return null;

            case (null, not null):
                return "Scenario succeeded under stock EF but threw under EF.Bulk:"
                    + Environment.NewLine + bulk;

            case (not null, null):
                return "Scenario threw under stock EF but succeeded under EF.Bulk:"
                    + Environment.NewLine + stock;
        }

        if (DescribeTypeMismatch(stock!, bulk!) is { } typeDiff)
        {
            return typeDiff + Environment.NewLine + Both(stock!, bulk!);
        }

        if (messagesMayDiffer is null
            && !string.Equals(stock!.Message, bulk!.Message, StringComparison.Ordinal))
        {
            return $"Both sides threw {stock.GetType().Name}, but with different messages."
                + Environment.NewLine + Both(stock, bulk);
        }

        return null;
    }

    /// <summary>Compares the two exceptions' types, and those of every exception they wrap.</summary>
    private static string? DescribeTypeMismatch(Exception stock, Exception bulk)
    {
        var stockLink = (Exception?)stock;
        var bulkLink = (Exception?)bulk;

        for (var depth = 0; stockLink is not null || bulkLink is not null; depth++)
        {
            if (stockLink is null || bulkLink is null)
            {
                return "The two failures wrap different numbers of exceptions: at nesting depth "
                    + $"{depth} stock EF has {Name(stockLink)} and EF.Bulk has {Name(bulkLink)}.";
            }

            if (stockLink.GetType() != bulkLink.GetType())
            {
                var where = depth == 0 ? "" : $" at nesting depth {depth}";

                return $"Stock EF threw {Name(stockLink)} but EF.Bulk threw {Name(bulkLink)}{where}.";
            }

            stockLink = stockLink.InnerException;
            bulkLink = bulkLink.InnerException;
        }

        return null;
    }

    private static string Name(Exception? exception)
        => exception is null ? "nothing" : exception.GetType().Name;

    private static string Both(Exception stock, Exception bulk)
        => "stock: " + stock + Environment.NewLine + "bulk : " + bulk;
}
