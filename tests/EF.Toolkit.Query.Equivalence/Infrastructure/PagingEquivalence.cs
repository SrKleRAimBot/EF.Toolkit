using EFToolkit.Query.Equivalence.Model;
using EFToolkit.Query.Paging;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Equivalence.Infrastructure;

/// <summary>
///     Walks a result set three ways and asserts the three agree: keyset forward, offset page by
///     page, and the ordering itself read in one go.
/// </summary>
/// <remarks>
///     <para>
///         This is the assertion the whole keyset implementation rests on. Any of the ways it can go
///         wrong — a mis-built lexicographic expansion, a direction flipped in the wrong place, a
///         cursor value that loses precision on the way through a URL — shows up here as a row seen
///         twice, a row never seen, or a row seen out of order, on whichever engine the fixture is
///         bound to.
///     </para>
///     <para>
///         Every divergence is reported together rather than failing on the first, because the three
///         together usually say what went wrong and the first alone rarely does.
///     </para>
/// </remarks>
public static class PagingEquivalence
{
    /// <summary>
    ///     Asserts that walking <paramref name="source" /> by keyset covers exactly the rows the
    ///     ordering produces, in exactly that order, and that offset paging over the same ordering
    ///     agrees.
    /// </summary>
    /// <param name="context">The context to query through.</param>
    /// <param name="source">The query to walk. Must not be ordered — the definition orders it.</param>
    /// <param name="keys">The ordering to walk along.</param>
    /// <param name="identify">Reads a stable identity off a row, for reporting and comparison.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <param name="sabotage">
    ///     Corrupts the keyset walk before it is compared. Used only by the harness self-tests, which
    ///     exist so that "this assertion would catch a lost row" is a claim something checks rather
    ///     than a claim in a comment.
    /// </param>
    public static async Task AssertAsync<T, TId>(
        ShopContext context,
        IQueryable<T> source,
        KeysetDefinition<T> keys,
        Func<T, TId> identify,
        int pageSize,
        CancellationToken cancellationToken,
        Func<List<TId>, List<TId>>? sabotage = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(identify);

        var expected = (await keys.Order(source).ToListAsync(cancellationToken))
            .Select(identify)
            .ToList();

        var byKeyset = await WalkForwardAsync(context, source, keys, identify, pageSize, cancellationToken);

        if (sabotage is not null)
        {
            byKeyset = sabotage(byKeyset);
        }
        var byOffset = await WalkByOffsetAsync(source, keys, identify, pageSize, cancellationToken);
        var backwards = await WalkBackwardAsync(context, source, keys, identify, pageSize, cancellationToken);

        var failures = new List<string>();

        if (!byKeyset.SequenceEqual(expected))
        {
            failures.Add(Describe("Keyset paging diverged from the ordering", expected, byKeyset));
        }

        if (!byOffset.SequenceEqual(expected))
        {
            failures.Add(Describe("Offset paging diverged from the ordering", expected, byOffset));
        }

        if (!backwards.SequenceEqual(expected))
        {
            failures.Add(Describe(
                "Walking backwards from the end did not retrace the forward walk",
                expected,
                backwards));
        }

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine + Environment.NewLine, failures));
        }
    }

    /// <summary>Reads every page forward, following <see cref="KeysetPage{T}.Next" />.</summary>
    public static async Task<List<TId>> WalkForwardAsync<T, TId>(
        ShopContext context,
        IQueryable<T> source,
        KeysetDefinition<T> keys,
        Func<T, TId> identify,
        int pageSize,
        CancellationToken cancellationToken)
        where T : class
    {
        var visited = new List<TId>();
        KeysetCursor? cursor = null;

        // Bounded so a cursor that fails to advance ends the test with a clear message rather than
        // running until the suite times out.
        for (var page = 0; page <= MaxPages(pageSize); page++)
        {
            var result = await source.ToKeysetPageAsync(context, keys, pageSize, cursor, cancellationToken);

            visited.AddRange(result.Items.Select(identify));

            result.Items.Count.ShouldBeLessThanOrEqualTo(pageSize);

            if (!result.HasNext || result.Next is null)
            {
                return visited;
            }

            result.Items.ShouldNotBeEmpty("a page reporting more to come must have returned something");
            cursor = result.Next;
        }

        Assert.Fail($"Keyset paging did not terminate within {MaxPages(pageSize)} pages.");
        return visited;
    }

    /// <summary>
    ///     Walks to the last page, then reads backwards, and returns the rows in forward order.
    /// </summary>
    private static async Task<List<TId>> WalkBackwardAsync<T, TId>(
        ShopContext context,
        IQueryable<T> source,
        KeysetDefinition<T> keys,
        Func<T, TId> identify,
        int pageSize,
        CancellationToken cancellationToken)
        where T : class
    {
        // Find the last page by walking forward, then retrace. Reading backwards is where a direction
        // flip that was applied to the comparison but not the ORDER BY (or the other way round) turns
        // into a page that overlaps the one before it.
        KeysetCursor? cursor = null;
        KeysetPage<T>? last = null;

        for (var page = 0; page <= MaxPages(pageSize); page++)
        {
            last = await source.ToKeysetPageAsync(context, keys, pageSize, cursor, cancellationToken);

            if (!last.HasNext || last.Next is null)
            {
                break;
            }

            cursor = last.Next;
        }

        var visited = new List<TId>();

        if (last is null)
        {
            return visited;
        }

        visited.AddRange(last.Items.Select(identify));
        cursor = last.Previous;

        for (var page = 0; page <= MaxPages(pageSize) && cursor is not null; page++)
        {
            var result = await source.ToKeysetPageAsync(context, keys, pageSize, cursor, cancellationToken);

            // Prepended: each backward page sits before everything gathered so far.
            visited.InsertRange(0, result.Items.Select(identify));

            if (!result.HasPrevious || result.Previous is null)
            {
                break;
            }

            cursor = result.Previous;
        }

        return visited;
    }

    private static async Task<List<TId>> WalkByOffsetAsync<T, TId>(
        IQueryable<T> source,
        KeysetDefinition<T> keys,
        Func<T, TId> identify,
        int pageSize,
        CancellationToken cancellationToken)
        where T : class
    {
        var ordered = keys.Order(source);
        var visited = new List<TId>();

        for (var pageNumber = 1; pageNumber <= MaxPages(pageSize); pageNumber++)
        {
            var page = await ordered.ToPagedResultAsync(
                PageRequest.Of(pageNumber, pageSize),
                cancellationToken);

            visited.AddRange(page.Items.Select(identify));

            if (page.HasNext != true)
            {
                break;
            }
        }

        return visited;
    }

    private static int MaxPages(int pageSize) => (10_000 / Math.Max(pageSize, 1)) + 10;

    private static string Describe<TId>(string what, List<TId> expected, List<TId> actual)
    {
        var missing = expected.Except(actual).ToArray();
        var extra = actual.Except(expected).ToArray();
        var duplicated = actual.GroupBy(static x => x)
            .Where(static g => g.Count() > 1)
            .Select(static g => g.Key)
            .ToArray();

        var lines = new List<string>
        {
            $"{what}.",
            $"  expected {expected.Count} row(s), saw {actual.Count}",
        };

        if (missing.Length > 0)
        {
            lines.Add($"  never returned: {string.Join(", ", missing.Take(20))}");
        }

        if (extra.Length > 0)
        {
            lines.Add($"  returned but not in the ordering: {string.Join(", ", extra.Take(20))}");
        }

        if (duplicated.Length > 0)
        {
            lines.Add($"  returned more than once: {string.Join(", ", duplicated.Take(20))}");
        }

        if (missing.Length == 0 && extra.Length == 0 && duplicated.Length == 0)
        {
            lines.Add("  same rows, different order");
            lines.Add($"  expected: {string.Join(", ", expected.Take(20))}");
            lines.Add($"  actual:   {string.Join(", ", actual.Take(20))}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
