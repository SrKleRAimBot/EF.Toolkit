using System.Text;
using EFToolkit.Audit.Equivalence.Model;

namespace EFToolkit.Audit.Equivalence.Infrastructure;

/// <summary>
///     Asserts that the same logical change, written two different ways, is audited identically.
/// </summary>
/// <remarks>
///     <para>
///         This is the gate the whole design exists to pass. The <c>SaveChanges</c> path reads the
///         change tracker; the explicit bulk path reads detached objects and a row it fetched
///         itself. They share nothing but the entry factory, and the claim being made is that they
///         are nonetheless indistinguishable in the trail they leave.
///     </para>
///     <para>
///         Everything that legitimately differs is excluded from the comparison, and the list is
///         short on purpose: the entry's generated key, its timestamp, its correlation id, and the
///         source column that names the path. Anything else differing is a real divergence.
///     </para>
/// </remarks>
public static class AuditEquivalence
{
    /// <summary>Runs a scenario twice and compares the audit entries it produced.</summary>
    /// <param name="fixture">The database, reset before each run.</param>
    /// <param name="throughSaveChanges">The change, made through <c>SaveChanges()</c>.</param>
    /// <param name="throughBulk">The same change, made through the explicit bulk API.</param>
    public static async Task AssertAsync(
        AuditDatabaseFixture fixture,
        Func<ShopContext, Task> throughSaveChanges,
        Func<ShopContext, Task> throughBulk)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(throughSaveChanges);
        ArgumentNullException.ThrowIfNull(throughBulk);

        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        var expected = await RunAsync(fixture, throughSaveChanges, bulk: false);
        var actual = await RunAsync(fixture, throughBulk, bulk: true);

        Compare(expected, actual);
    }

    private static async Task<List<AuditRow>> RunAsync(
        AuditDatabaseFixture fixture,
        Func<ShopContext, Task> scenario,
        bool bulk)
    {
        await fixture.ResetAsync();

        await using (var context = fixture.CreateContext(bulk: bulk))
        {
            await scenario(context);
        }

        return await AuditSnapshot.ReadAsync(fixture);
    }

    private static void Compare(List<AuditRow> expected, List<AuditRow> actual)
    {
        var divergences = new List<string>();

        if (expected.Count != actual.Count)
        {
            divergences.Add(
                $"SaveChanges produced {expected.Count} audit entries, the bulk API produced "
                + $"{actual.Count}.");
        }

        for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            if (expected[i].SortKey != actual[i].SortKey)
            {
                divergences.Add(
                    $"Entry {i} differs.{Environment.NewLine}"
                    + $"  SaveChanges: {expected[i].SortKey}{Environment.NewLine}"
                    + $"  Bulk:        {actual[i].SortKey}");
            }
        }

        if (divergences.Count == 0)
        {
            return;
        }

        // Every divergence at once. Fixing them one failure at a time is how a difference in the
        // fourth entry stays hidden behind a difference in the first.
        var message = new StringBuilder("Audit entries diverge between write paths:")
            .AppendLine()
            .AppendJoin(Environment.NewLine, divergences);

        Assert.Fail(message.ToString());
    }
}
