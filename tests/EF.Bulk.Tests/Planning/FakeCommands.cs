using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace EFBulk.Tests.Planning;

/// <summary>
///     Minimal stand-ins for EF's modification commands, so partitioning can be tested without a
///     database or a model. Only the members the partitioner reads are implemented.
/// </summary>
internal sealed class FakeCommand : IReadOnlyModificationCommand
{
    public string TableName { get; init; } = "T";
    public string? Schema { get; init; }
    public EntityState EntityState { get; init; } = EntityState.Added;
    public IReadOnlyList<IColumnModification> ColumnModifications { get; init; } = [];
    public IStoreStoredProcedure? StoreStoredProcedure { get; init; }
    public IColumnBase? RowsAffectedColumn { get; init; }

    public ITable? Table => null;
    public IReadOnlyList<IUpdateEntry> Entries => [];

    public void PropagateResults(RelationalDataReader relationalReader)
        => throw new NotSupportedException();

    public void PropagateOutputParameters(DbParameterCollection parameterCollection, int baseParameterIndex)
        => throw new NotSupportedException();

    /// <summary>Builds an INSERT-shaped command writing <paramref name="writeColumns" />.</summary>
    public static FakeCommand Insert(string table, params string[] writeColumns)
        => new()
        {
            TableName = table,
            EntityState = EntityState.Added,
            ColumnModifications = [.. writeColumns.Select(c => new FakeColumn { ColumnName = c, IsWrite = true })]
        };

    /// <summary>Builds an UPDATE-shaped command writing <paramref name="writeColumns" />.</summary>
    public static FakeCommand Update(string table, params string[] writeColumns)
        => new()
        {
            TableName = table,
            EntityState = EntityState.Modified,
            ColumnModifications = [.. writeColumns.Select(c => new FakeColumn { ColumnName = c, IsWrite = true })]
        };

    /// <summary>Builds a DELETE-shaped command keyed on <paramref name="keyColumns" />.</summary>
    public static FakeCommand Delete(string table, params string[] keyColumns)
        => new()
        {
            TableName = table,
            EntityState = EntityState.Deleted,
            ColumnModifications =
                [.. keyColumns.Select(c => new FakeColumn { ColumnName = c, IsCondition = true, IsKey = true })]
        };
}

internal sealed class FakeColumn : IColumnModification
{
    public string ColumnName { get; init; } = "C";
    public bool IsRead { get; set; }
    public bool IsWrite { get; set; }
    public bool IsCondition { get; set; }
    public bool IsKey { get; set; }
    public string? JsonPath { get; init; }
    public object? Value { get; set; }
    public object? OriginalValue { get; set; }

    public IUpdateEntry? Entry => null;
    public IProperty? Property => null;
    public IColumnBase? Column => null;
    public RelationalTypeMapping? TypeMapping => null;
    public bool? IsNullable => null;
    public bool UseOriginalValueParameter => false;
    public bool UseCurrentValueParameter => false;
    public bool UseOriginalValue => false;
    public bool UseCurrentValue => false;
    public bool UseParameter => false;
    public string? ParameterName => null;
    public string? OriginalParameterName => null;
    public string? ColumnType => null;

    public void AddSharedColumnModification(IColumnModification modification)
        => throw new NotSupportedException();

    public void ResetParameterNames()
        => throw new NotSupportedException();
}
