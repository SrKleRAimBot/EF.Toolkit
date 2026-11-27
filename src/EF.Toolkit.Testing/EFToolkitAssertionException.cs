namespace EFToolkit.Testing;

/// <summary>A test assertion made by EF.Toolkit.Testing did not hold.</summary>
/// <remarks>
///     A type of the package's own rather than a test framework's, so the package works under any of
///     them. Every runner reports an unexpected exception as a failure with its message, which is all
///     an assertion failure needs to be.
/// </remarks>
public sealed class EFToolkitAssertionException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public EFToolkitAssertionException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was expected and what was found.</param>
    public EFToolkitAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was expected and what was found.</param>
    /// <param name="innerException">The underlying cause.</param>
    public EFToolkitAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
