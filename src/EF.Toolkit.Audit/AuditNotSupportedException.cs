namespace EFToolkit.Audit;

/// <summary>
///     Thrown when auditing has been asked for something it cannot honour.
/// </summary>
/// <remarks>
///     Always a refusal, never a fallback. An audit trail that quietly records less than it was
///     configured to record is worse than one that fails loudly, because the gap is invisible until
///     somebody needs the missing entry.
/// </remarks>
public class AuditNotSupportedException : InvalidOperationException
{
    /// <summary>Initializes a new instance with a default message.</summary>
    public AuditNotSupportedException()
        : base("The requested auditing behaviour is not supported.")
    {
    }

    /// <summary>Initializes a new instance with the given message.</summary>
    /// <param name="message">Why the request was refused.</param>
    public AuditNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the given message and cause.</summary>
    /// <param name="message">Why the request was refused.</param>
    /// <param name="innerException">The underlying failure.</param>
    public AuditNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
