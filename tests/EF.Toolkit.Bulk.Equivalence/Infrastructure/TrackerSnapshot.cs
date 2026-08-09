using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace EFToolkit.Bulk.Equivalence.Infrastructure;

/// <summary>
///     The state of every entity in the change tracker after a scenario has run.
/// </summary>
/// <remarks>
///     "Retains EF's change tracking" is the load-bearing claim of the whole package, so it is
///     asserted rather than assumed. Original values are captured as well as current ones: an
///     entity that ends <c>Unchanged</c> but with a stale original-values snapshot would produce
///     wrong UPDATE statements on the next save, and nothing else would notice.
/// </remarks>
public sealed class TrackerSnapshot
{
    private readonly List<EntrySnapshot> _entries;

    private TrackerSnapshot(List<EntrySnapshot> entries)
        => _entries = entries;

    private sealed record EntrySnapshot(
        string EntityType,
        string Key,
        EntityState State,
        SortedDictionary<string, string> CurrentValues,
        SortedDictionary<string, string> OriginalValues);

    /// <summary>Captures the current state of <paramref name="context" />'s change tracker.</summary>
    public static TrackerSnapshot Capture(DbContext context)
    {
        var entries = new List<EntrySnapshot>();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            var entityType = entry.Metadata.Name;
            var key = string.Join(
                "|",
                entry.Metadata.FindPrimaryKey()?.Properties
                    .Select(p => FormatProperty(entry.Property(p.Name)))
                ?? []);

            var current = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var original = new SortedDictionary<string, string>(StringComparer.Ordinal);

            foreach (var property in entry.Metadata.GetProperties())
            {
                var propertyEntry = entry.Property(property.Name);
                current[property.Name] = FormatProperty(propertyEntry);

                // Original values are unavailable on a Detached or Added entry; recording the
                // state itself keeps the comparison meaningful without special-casing.
                original[property.Name] = entry.State is EntityState.Detached or EntityState.Added
                    ? "(n/a)"
                    : Format(propertyEntry.OriginalValue);
            }

            entries.Add(new EntrySnapshot(entityType, key, entry.State, current, original));
        }

        // Temporary keys are normalised away above, so entries whose insert never completed all
        // share the key "(temp)". Ordering falls through to the remaining current values, which do
        // distinguish them — without that tie-break the comparison would depend on the change
        // tracker's enumeration order, which EF does not guarantee.
        entries.Sort((a, b) =>
        {
            var byType = string.CompareOrdinal(a.EntityType, b.EntityType);
            if (byType != 0)
            {
                return byType;
            }

            var byKey = string.CompareOrdinal(a.Key, b.Key);
            return byKey != 0
                ? byKey
                : string.CompareOrdinal(Flatten(a.CurrentValues), Flatten(b.CurrentValues));
        });

        return new TrackerSnapshot(entries);
    }

    /// <summary>
    ///     Describes the first difference against <paramref name="other" />, or
    ///     <see langword="null" /> when the two snapshots are equivalent.
    /// </summary>
    public string? Diff(TrackerSnapshot other)
    {
        if (_entries.Count != other._entries.Count)
        {
            return $"Change tracker holds {_entries.Count} entr(ies) under stock EF but "
                + $"{other._entries.Count} under EF.Toolkit.Bulk."
                + Environment.NewLine
                + "  stock: " + Describe(_entries)
                + Environment.NewLine
                + "  bulk : " + Describe(other._entries);
        }

        for (var i = 0; i < _entries.Count; i++)
        {
            var expected = _entries[i];
            var actual = other._entries[i];

            if (expected.EntityType != actual.EntityType || expected.Key != actual.Key)
            {
                return $"Change tracker entry {i} is {expected.EntityType}[{expected.Key}] under "
                    + $"stock EF but {actual.EntityType}[{actual.Key}] under EF.Toolkit.Bulk.";
            }

            if (expected.State != actual.State)
            {
                return $"{expected.EntityType}[{expected.Key}] is {expected.State} under stock EF "
                    + $"but {actual.State} under EF.Toolkit.Bulk.";
            }

            if (Diff(expected, actual, expected.CurrentValues, actual.CurrentValues, "current")
                is { } currentDiff)
            {
                return currentDiff;
            }

            if (Diff(expected, actual, expected.OriginalValues, actual.OriginalValues, "original")
                is { } originalDiff)
            {
                return originalDiff;
            }
        }

        return null;
    }

    private static string? Diff(
        EntrySnapshot expected,
        EntrySnapshot actual,
        SortedDictionary<string, string> expectedValues,
        SortedDictionary<string, string> actualValues,
        string kind)
    {
        foreach (var (property, expectedValue) in expectedValues)
        {
            if (!actualValues.TryGetValue(property, out var actualValue))
            {
                return $"{expected.EntityType}[{expected.Key}]: property '{property}' is missing "
                    + "from the EF.Toolkit.Bulk change tracker entry.";
            }

            if (expectedValue != actualValue)
            {
                return $"{actual.EntityType}[{actual.Key}].{property} {kind} value is "
                    + $"{expectedValue} under stock EF but {actualValue} under EF.Toolkit.Bulk.";
            }
        }

        return null;
    }

    private static string Describe(List<EntrySnapshot> entries)
        => entries.Count == 0
            ? "(empty)"
            : string.Join(", ", entries.Select(e => $"{Simple(e.EntityType)}[{e.Key}]:{e.State}"));

    private static string Simple(string entityTypeName)
        => entityTypeName[(entityTypeName.LastIndexOf('.') + 1)..];

    private static string Flatten(SortedDictionary<string, string> values)
        => string.Join("|", values.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>
    ///     Formats a property value, collapsing EF's temporary key values to a constant.
    /// </summary>
    /// <remarks>
    ///     EF hands out temporary keys from a per-context counter, so their actual values depend on
    ///     how much that particular context has done and differ between two runs of the same
    ///     scenario. They are an implementation detail with no observable meaning — comparing them
    ///     would fail whenever a save threw with entities still Added.
    /// </remarks>
    private static string FormatProperty(PropertyEntry propertyEntry)
        => propertyEntry.IsTemporary ? "(temp)" : Format(propertyEntry.CurrentValue);

    private static string Format(object? value)
        => value switch
        {
            null => "NULL",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            decimal d => d.ToString("0.############################", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
}
