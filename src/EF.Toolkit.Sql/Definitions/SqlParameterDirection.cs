namespace EFToolkit.Sql.Definitions;

/// <summary>The direction of a stored procedure parameter.</summary>
public enum SqlParameterDirection
{
    /// <summary>Passed in by the caller.</summary>
    In,

    /// <summary>
    ///     Set by the procedure. SQL Server has no output-only parameters, so there this renders as
    ///     <c>OUTPUT</c>, the same as <see cref="InOut" />.
    /// </summary>
    Out,

    /// <summary>Passed in by the caller and may be changed by the procedure.</summary>
    InOut,
}
