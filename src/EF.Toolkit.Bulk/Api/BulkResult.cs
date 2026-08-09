namespace EFToolkit.Bulk.Api;

/// <summary>
///     What a bulk operation did.
/// </summary>
public sealed record BulkResult
{
    /// <summary>Rows inserted.</summary>
    public int Inserted { get; init; }

    /// <summary>Rows updated.</summary>
    public int Updated { get; init; }

    /// <summary>Rows deleted.</summary>
    public int Deleted { get; init; }

    /// <summary>Total rows affected.</summary>
    public int Total => Inserted + Updated + Deleted;

    /// <summary>A result describing an insert of <paramref name="rows" /> rows.</summary>
    /// <param name="rows">Number of rows inserted.</param>
    public static BulkResult ForInsert(int rows) => new() { Inserted = rows };
}
