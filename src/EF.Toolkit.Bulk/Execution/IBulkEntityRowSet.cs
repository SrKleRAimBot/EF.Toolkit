namespace EFToolkit.Bulk.Execution;

/// <summary>
///     A row set whose rows are CLR entities, so a provider can compile a path straight from a
///     property to the wire.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="IBulkRowSet.GetValue" /> returns <see cref="object" />, which is the only
///         thing it could return: the transparent path reads values out of EF's modification
///         commands, where they are already boxed. The explicit API is different — its values live
///         in typed properties on entities, and boxing them only to unbox them again on the way to
///         a strongly-typed driver call is pure waste, thirty times per row on a wide table.
///     </para>
///     <para>
///         A provider that can exploit that tests for this interface and falls back to the boxed
///         path when a row set does not implement it. Deliberately non-generic: making
///         <see cref="IBulkRowSet" /> generic would force the same generic parameter through
///         <see cref="IBulkOperationExecutor" />, which the transparent path could only satisfy
///         with <c>object</c>, and the single cast inside a compiled accessor is far cheaper than
///         that would be to reason about.
///     </para>
/// </remarks>
public interface IBulkEntityRowSet : IBulkRowSet
{
    /// <summary>CLR type of the entities backing these rows.</summary>
    Type EntityClrType { get; }

    /// <summary>The entities, in row order.</summary>
    IReadOnlyList<object> Entities { get; }
}
