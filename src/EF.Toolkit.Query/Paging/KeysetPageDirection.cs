namespace EFToolkit.Query.Paging;

/// <summary>Which way a cursor reads from its boundary row.</summary>
public enum KeysetPageDirection
{
    /// <summary>Rows after the boundary, in the definition's own order.</summary>
    Forward = 0,

    /// <summary>
    ///     Rows before the boundary. Read with every comparison and every <c>ORDER BY</c> reversed,
    ///     then handed back in the definition's order.
    /// </summary>
    Backward = 1,
}
