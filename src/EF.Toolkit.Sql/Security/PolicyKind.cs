namespace EFToolkit.Sql.Security;

/// <summary>How a PostgreSQL policy combines with the other policies on its table.</summary>
public enum PolicyKind
{
    /// <summary>
    ///     Combined with the table's other permissive policies by <c>OR</c>: a row is visible if any
    ///     permissive policy allows it. A table with RLS enabled and no permissive policy shows nothing.
    /// </summary>
    Permissive,

    /// <summary>
    ///     Combined by <c>AND</c> with everything else: a row must pass every restrictive policy as
    ///     well as at least one permissive one.
    /// </summary>
    Restrictive,
}
