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

        RestoreClearedComplexValues(entry.ComplexProperties, entry.OriginalValues);
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

    // An optional complex value set to null before Remove() is an edit like any other. Comparing a null
    // value against its snapshot is where EF Core versions differ — 11 writes the nulls even with the
    // snapshot replaced — so the value is rebuilt from its members' original values instead, before the
    // snapshot is. The entity is being deleted, so reverting it in memory costs the application nothing.
    private static void RestoreClearedComplexValues(IEnumerable<ComplexPropertyEntry> complexProperties, PropertyValues originals)
    {
        foreach (var complex in complexProperties)
        {
            if (complex.CurrentValue is not null)
            {
                RestoreClearedComplexValues(complex.ComplexProperties, originals);
                continue;
            }

            var members = complex.Metadata.ComplexType.GetProperties().ToList();
            if (!complex.Metadata.IsNullable || members.All(p => originals[p] is null))
            {
                continue;
            }

            var value = Activator.CreateInstance(complex.Metadata.ClrType)!;
            foreach (var member in members)
            {
                member.PropertyInfo?.SetValue(value, originals[member]);
            }

            complex.CurrentValue = value;
        }
    }
}
