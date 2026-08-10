using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     Walks a <see cref="IBulkRowSet" /> one row at a time, materialising each row into a reused
///     buffer that is ready for the wire.
/// </summary>
/// <remarks>
///     <para>
///         Reading a cell used to cost more than it looks. <c>SqlBulkCopy</c> asks
///         <c>IsDBNull</c> before <c>GetValue</c>, and answering the first by fetching the value
///         meant every cell was read — and every value converter run — twice. Underneath that,
///         each read went through an interface call on the row set, an interface indexer on the
///         column list, and a virtual converter call even for the overwhelming majority of columns
///         that have no converter.
///     </para>
///     <para>
///         Filling the whole row once collapses all of that: the per-column decisions are made in
///         the constructor and stored in flat arrays, so the row loop is array indexing and one
///         branch per cell, and a converter is only invoked where one exists.
///     </para>
///     <para>
///         Nulls are kept as <see langword="null" /> rather than <see cref="DBNull" />. The
///         PostgreSQL copy path needs to distinguish them to call <c>WriteNull</c>, and turning a
///         null into <see cref="DBNull" /> is one coalesce at the single point that wants it.
///     </para>
/// </remarks>
internal sealed class BulkRowCursor
{
    private readonly IBulkRowSet _rows;
    private readonly int[] _sourceIndex;
    private readonly bool[] _useOriginal;
    private readonly ValueConverter?[] _converters;
    private readonly object?[] _buffer;
    private readonly int _stagedCount;
    private readonly int _ordinalSlot;

    private int _row = -1;

    /// <summary>Creates a cursor over <paramref name="rows" /> in the given staging layout.</summary>
    /// <param name="rows">The rows to walk.</param>
    /// <param name="layout">The staged columns, in write order.</param>
    /// <param name="includeOrdinal">
    ///     Whether to append a synthetic trailing slot carrying each row's position, used to
    ///     correlate server-generated values back to the row that produced them.
    /// </param>
    public BulkRowCursor(
        IBulkRowSet rows,
        IReadOnlyList<StagingColumn> layout,
        bool includeOrdinal = false)
    {
        _rows = rows;
        _stagedCount = layout.Count;
        _ordinalSlot = includeOrdinal ? layout.Count : -1;

        var count = layout.Count + (includeOrdinal ? 1 : 0);

        _sourceIndex = new int[count];
        _useOriginal = new bool[count];
        _converters = new ValueConverter?[count];
        _buffer = new object?[count];

        for (var i = 0; i < layout.Count; i++)
        {
            _sourceIndex[i] = layout[i].Index;
            _useOriginal[i] = layout[i].UseOriginal;
            _converters[i] = rows.Columns[layout[i].Index].TypeMapping?.Converter;
        }
    }

    /// <summary>Number of slots in a row, including the ordinal if there is one.</summary>
    public int FieldCount => _buffer.Length;

    /// <summary>Index of the row currently materialised, or -1 before the first.</summary>
    public int Row => _row;

    /// <summary>Advances to the next row and materialises it.</summary>
    /// <returns><see langword="false" /> once the rows are exhausted.</returns>
    public bool MoveNext()
    {
        var row = _row + 1;
        if (row >= _rows.RowCount)
        {
            // Still advance, so a reader asking whether it is closed gets a stable answer.
            _row = _rows.RowCount;
            return false;
        }

        _row = row;
        Fill(row);
        return true;
    }

    /// <summary>The provider-ready value in <paramref name="slot" />, or null.</summary>
    public object? this[int slot] => _buffer[slot];

    /// <summary>Whether <paramref name="slot" /> holds a null.</summary>
    public bool IsNull(int slot) => _buffer[slot] is null;

    /// <summary>Whether every row has been read.</summary>
    public bool IsExhausted => _row >= _rows.RowCount;

    private void Fill(int row)
    {
        // Hoisted so the loop indexes arrays rather than re-reading fields through `this`.
        var rows = _rows;
        var sourceIndex = _sourceIndex;
        var useOriginal = _useOriginal;
        var converters = _converters;
        var buffer = _buffer;

        for (var i = 0; i < _stagedCount; i++)
        {
            var column = sourceIndex[i];

            var value = useOriginal[i]
                ? rows.GetOriginalValue(row, column)
                : rows.GetValue(row, column);

            if (value is not null && converters[i] is { } converter)
            {
                value = converter.ConvertToProvider(value);
            }

            buffer[i] = value;
        }

        if (_ordinalSlot >= 0)
        {
            buffer[_ordinalSlot] = row;
        }
    }
}
