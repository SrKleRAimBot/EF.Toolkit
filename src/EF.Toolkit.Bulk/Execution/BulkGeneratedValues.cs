namespace EFToolkit.Bulk.Execution;

/// <summary>
///     Holds store-generated values until the operation that produced them has finished.
/// </summary>
/// <remarks>
///     <para>
///         An executor that writes row by row learns each row's generated values as it goes, long
///         before it knows whether the whole operation will succeed. Assigning them on arrival would
///         leave real keys on entities whose rows a later failure rolls back — where stock EF leaves
///         a failed save's entities exactly as they were.
///     </para>
///     <para>
///         So they are buffered here and written back in one pass once the last statement has
///         returned, the same point at which the bulk-copy executors assign theirs.
///     </para>
/// </remarks>
internal sealed class BulkGeneratedValues
{
    private readonly List<(int Row, int Column, object? Value)> _values = [];

    /// <summary>Records a value for <paramref name="row" />'s <paramref name="column" />.</summary>
    public void Add(int row, int column, object? value) => _values.Add((row, column, value));

    /// <summary>Writes every recorded value back onto <paramref name="rows" />, in arrival order.</summary>
    public void ApplyTo(IBulkRowSet rows)
    {
        foreach (var (row, column, value) in _values)
        {
            rows.SetGeneratedValue(row, column, value);
        }
    }
}
