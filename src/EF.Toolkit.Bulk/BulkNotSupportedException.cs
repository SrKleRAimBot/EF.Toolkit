using EFToolkit.Bulk.Configuration;

namespace EFToolkit.Bulk;

/// <summary>
///     Thrown when EF.Toolkit.Bulk cannot accelerate a write and
///     <see cref="Unsupported.Throw" /> is configured, or when a requested bulk
///     operation is impossible on the target database.
/// </summary>
public sealed class BulkNotSupportedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="BulkNotSupportedException" /> class.</summary>
    /// <param name="message">A description of why the operation could not be accelerated.</param>
    public BulkNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="BulkNotSupportedException" /> class.</summary>
    /// <param name="message">A description of why the operation could not be accelerated.</param>
    /// <param name="innerException">The underlying cause.</param>
    public BulkNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
