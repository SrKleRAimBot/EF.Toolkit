using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace EFToolkit.Conventions.Saving;

/// <summary>Accepts an entry's current values as its baseline, so EF's next change detection finds nothing to write.</summary>
/// <remarks>
///     <para>
///         A soft delete writes the soft-delete column and nothing else. Edits the application made
///         before <c>Remove()</c> would otherwise ride along: EF's own change detection runs after the
///         conversion and compares every member against its original snapshot — scalars, complex
///         values, optional complex values set to <see langword="null" />, complex collections. A real
///         delete would have discarded them all, so the soft delete does too.
///     </para>
///     <para>
///         The whole snapshot is replaced at once through <c>OriginalValues.SetValues</c>, which EF
///         itself defines over every kind of member, rather than member by member, where each kind of
///         member left out is an edit that still gets written. Concurrency tokens keep their original
///         values: those are what the <c>UPDATE</c>'s <c>WHERE</c> clause has to check.
///     </para>
/// </remarks>
internal static class Baseline
{
    internal static void Accept(EntityEntry entry)
    {
        var tokens = entry.Properties
            .Where(p => p.Metadata.IsConcurrencyToken)
            .Select(p => (Property: p, Original: p.OriginalValue))
            .ToList();

        entry.OriginalValues.SetValues(entry.CurrentValues);

        foreach (var (property, original) in tokens)
        {
            property.OriginalValue = original;
        }

        foreach (var property in entry.Properties)
        {
            property.IsModified = false;
        }
    }
}
