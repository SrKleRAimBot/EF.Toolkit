namespace EFToolkit.Query;

/// <summary>
///     Thrown when a query cannot be expressed through EF.Toolkit.Query in a way that is guaranteed
///     to be correct.
/// </summary>
/// <remarks>
///     Every message names the way out. The alternative to refusing is a paginated query that
///     silently skips or repeats rows, which surfaces as a data-loss bug in the consuming
///     application long after the query was written.
/// </remarks>
public sealed class QueryNotSupportedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="QueryNotSupportedException" /> class.</summary>
    public QueryNotSupportedException()
        : base("The query is not supported by EF.Toolkit.Query.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="QueryNotSupportedException" /> class.</summary>
    /// <param name="message">Describes what was refused and how to proceed instead.</param>
    public QueryNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="QueryNotSupportedException" /> class.</summary>
    /// <param name="message">Describes what was refused and how to proceed instead.</param>
    /// <param name="innerException">The underlying failure.</param>
    public QueryNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
