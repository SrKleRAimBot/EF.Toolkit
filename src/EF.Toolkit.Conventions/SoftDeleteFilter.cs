namespace EFToolkit.Conventions;

/// <summary>The named query filter soft delete installs.</summary>
/// <remarks>
///     Named, so that it can be switched off on its own — <c>IgnoreQueryFilters([SoftDeleteFilter.Name])</c>,
///     or <c>IncludeDeleted()</c> — while a tenant filter or any other named filter on the same type
///     stays in force. EF Core does not allow an unnamed filter beside a named one, so other filters
///     on a soft-deletable type must be named too.
/// </remarks>
public static class SoftDeleteFilter
{
    /// <summary>The filter's name.</summary>
    public const string Name = "EFToolkit.SoftDelete";
}
