namespace EFToolkit.Bulk.Execution;

/// <summary>
///     One column of a staging table: which source column it carries, and whether it carries the
///     loaded value or the new one.
/// </summary>
/// <remarks>
///     A concurrency token appears <em>twice</em> in a staged update — once holding the value that
///     was loaded, to join on, and once holding the new value, to assign. They cannot share a
///     staging column, so the second is aliased.
/// </remarks>
/// <param name="Index">Index into <see cref="IBulkRowSet.Columns" />.</param>
/// <param name="UseOriginal">Whether to stage the loaded value rather than the new one.</param>
/// <param name="Name">The staging table's column name.</param>
internal readonly record struct StagingColumn(int Index, bool UseOriginal, string Name)
{
    /// <summary>Suffix distinguishing the new value of a column that is also a condition.</summary>
    public const string NewValueSuffix = "__efbulk_new";

    /// <summary>
    ///     Name of the synthetic staging column carrying each row's position in the source.
    /// </summary>
    /// <remarks>
    ///     Correlating returned rows by position rather than by key value is both cheaper and more
    ///     correct: it costs no string building, and it survives two source rows sharing a key,
    ///     which key-based correlation silently collapsed into one.
    /// </remarks>
    public const string OrdinalColumnName = "__efbulk_ord";

    /// <summary>Reads this column's value for <paramref name="row" />, converted for the provider.</summary>
    public object? ValueFor(IBulkRowSet rows, int row)
    {
        var column = rows.Columns[Index];
        var value = UseOriginal ? rows.GetOriginalValue(row, Index) : rows.GetValue(row, Index);

        return column.ToProviderValue(value);
    }

    /// <summary>
    ///     Builds the staging layout for an update: every condition column, plus every written
    ///     column, aliasing the new value where a column is both.
    /// </summary>
    public static List<StagingColumn> ForUpdate(
        IBulkRowSet rows,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> writeIndices)
    {
        var staged = new List<StagingColumn>(conditionIndices.Count + writeIndices.Count);

        foreach (var index in conditionIndices)
        {
            staged.Add(new StagingColumn(index, UseOriginal: true, rows.Columns[index].Name));
        }

        foreach (var index in writeIndices)
        {
            var name = rows.Columns[index].Name;
            var alsoCondition = conditionIndices.Contains(index);

            staged.Add(new StagingColumn(
                index,
                UseOriginal: false,
                alsoCondition ? name + NewValueSuffix : name));
        }

        return staged;
    }

    /// <summary>Builds the staging layout for a delete: the condition columns only.</summary>
    public static List<StagingColumn> ForDelete(IBulkRowSet rows, IReadOnlyList<int> conditionIndices)
        => [.. conditionIndices.Select(i => new StagingColumn(i, true, rows.Columns[i].Name))];

    /// <summary>Builds the staging layout for an insert or merge: the written columns.</summary>
    public static List<StagingColumn> ForWrite(IBulkRowSet rows, IReadOnlyList<int> writeIndices)
        => [.. writeIndices.Select(i => new StagingColumn(i, false, rows.Columns[i].Name))];
}
