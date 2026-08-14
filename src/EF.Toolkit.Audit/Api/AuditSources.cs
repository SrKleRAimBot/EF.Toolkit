namespace EFToolkit.Audit.Api;

/// <summary>
///     The values that appear in an audit entry's <c>Source</c> column.
/// </summary>
/// <remarks>
///     Not an enum, so that a capture path this package has never heard of can name itself without
///     a change here. These are the ones this package and EF.Toolkit.Audit.Bulk produce.
/// </remarks>
public static class AuditSources
{
    /// <summary>A change made through <c>SaveChanges()</c>, including transparent bulk mode.</summary>
    public const string SaveChanges = "SaveChanges";

    /// <summary>A change made through <c>BulkInsertAsync</c>.</summary>
    public const string BulkInsert = "Bulk.Insert";

    /// <summary>A change made through <c>BulkUpdateAsync</c>.</summary>
    public const string BulkUpdate = "Bulk.Update";

    /// <summary>A change made through <c>BulkDeleteAsync</c>.</summary>
    public const string BulkDelete = "Bulk.Delete";

    /// <summary>A change made through <c>BulkMergeAsync</c>.</summary>
    public const string BulkMerge = "Bulk.Merge";

    /// <summary>A change made through <c>BulkSynchronizeAsync</c>.</summary>
    public const string BulkSynchronize = "Bulk.Synchronize";
}
