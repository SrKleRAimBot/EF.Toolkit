using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     Records which source rows a staged statement reported back, by ordinal.
/// </summary>
/// <remarks>
///     <para>
///         A bulk statement returns one affected-row count for the whole set, while EF's per-row
///         statements can say precisely which row went missing. Recovering that detail is what lets
///         a concurrency conflict name the entities involved — so every staged update and delete
///         carries an ordinal through to its <c>OUTPUT</c> or <c>RETURNING</c> list, and this
///         records what came back.
///     </para>
///     <para>
///         Correlating by ordinal replaced correlating by a string built from each row's key
///         values. That cost a <see cref="System.Text.StringBuilder" /> and a string per row,
///         twice per operation — once to index the source rows and once per returned row — where a
///         <see cref="bool" /> array costs one byte per row and no hashing at all.
///     </para>
///     <para>
///         It also changes what happens when a caller-supplied list contains the same key twice.
///         A set-based join applies one of the two writes and discards the other; with key
///         correlation both source rows mapped to the one returned row, so the operation reported a
///         row count one short and said nothing about why. By ordinal the second row is visibly
///         unaccounted for, and the caller is told rather than left to notice.
///     </para>
/// </remarks>
internal sealed class BulkRowTally
{
    private readonly bool[] _seen;
    private int _count;

    /// <summary>Creates a tally over <paramref name="rowCount" /> source rows.</summary>
    public BulkRowTally(int rowCount) => _seen = new bool[rowCount];

    /// <summary>How many distinct source rows have been reported back.</summary>
    public int Count => _count;

    /// <summary>
    ///     Records that the row at <paramref name="ordinal" /> came back.
    /// </summary>
    /// <remarks>
    ///     Repeats are ignored rather than rejected: a non-key match condition can legitimately
    ///     have one source row match several target rows.
    /// </remarks>
    /// <param name="ordinal">The source row's position, as carried through the staging table.</param>
    public void Mark(int ordinal)
    {
        if ((uint)ordinal >= (uint)_seen.Length)
        {
            throw new BulkNotSupportedException(
                $"A staged statement returned ordinal {ordinal}, which is outside the "
                + $"{_seen.Length} row(s) that were written. This indicates a bug in "
                + "EF.Toolkit.Bulk's correlation.");
        }

        if (!_seen[ordinal])
        {
            _seen[ordinal] = true;
            _count++;
        }
    }

    /// <summary>
    ///     Throws a <see cref="DbUpdateConcurrencyException" /> naming the rows that never came
    ///     back, matching what stock EF Core reports when a row has gone missing.
    /// </summary>
    /// <param name="rows">The rows the statement was given.</param>
    public void ThrowIfAnyMissing(IBulkRowSet rows)
    {
        if (_count == rows.RowCount)
        {
            return;
        }

        var missing = new List<IUpdateEntry>();
        var missingCount = 0;

        for (var row = 0; row < rows.RowCount; row++)
        {
            if (!_seen[row])
            {
                missingCount++;
                missing.AddRange(rows.GetEntries(row));
            }
        }

        if (missingCount == 0)
        {
            return;
        }

        var verb = rows.EntityState == EntityState.Deleted ? "deleted" : "updated";

        throw new DbUpdateConcurrencyException(
            $"The database operation was expected to affect {rows.RowCount} row(s), but actually "
            + $"affected {_count}. Data may have been modified or deleted since "
            + $"the entities were loaded ({missingCount} row(s) could not be {verb}).",
            missing);
    }
}
