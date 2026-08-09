namespace EFToolkit.Bulk.Configuration;

/// <summary>
///     Controls what happens when EF.Toolkit.Bulk encounters a write it cannot accelerate.
/// </summary>
public enum Unsupported
{
    /// <summary>
    ///     Silently execute the affected partition through stock EF Core. Bulk acceleration is an
    ///     optimisation and must never change results, so this is the default.
    /// </summary>
    FallBack,

    /// <summary>
    ///     Throw <see cref="BulkNotSupportedException" /> instead of falling back.
    ///     <para>
    ///         Intended for tests and CI: it turns "this silently ran at stock EF speed" into a
    ///         visible failure, so a regression in the fast path cannot hide as a performance
    ///         problem nobody notices.
    ///     </para>
    /// </summary>
    Throw
}
