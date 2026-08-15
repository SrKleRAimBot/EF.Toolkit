namespace EFToolkit.Query.Sorting;

/// <summary>One term of a requested ordering: a field name and the direction to apply it in.</summary>
/// <param name="Name">
///     The external field name, which must be one a <see cref="SortSpecification{T}" /> allows.
///     Matched case-insensitively.
/// </param>
/// <param name="Direction">Which way to order by it.</param>
public readonly record struct SortField(string Name, SortDirection Direction)
{
    /// <summary>Renders the term in the <c>name:direction</c> form <see cref="SortRequest.Parse" /> accepts.</summary>
    /// <returns>The rendered term.</returns>
    public override string ToString()
        => Direction == SortDirection.Descending ? $"{Name}:desc" : $"{Name}:asc";
}
