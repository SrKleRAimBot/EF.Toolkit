namespace EFToolkit.Query.Equivalence.Model;

/// <summary>Stored as its underlying number, so a keyset ordering by it keeps the enum's own order.</summary>
public enum OrderStatus
{
    Placed = 0,
    Shipped = 1,
    Cancelled = 2,
}

/// <summary>
///     The main subject. Seeded so that <see cref="PlacedAt" /> and <see cref="Total" /> both carry
///     heavy duplication: a keyset predicate written as <c>a &gt; a0 &amp;&amp; b &gt; b0</c> passes
///     every test over distinct values and only loses rows once the leading column ties.
/// </summary>
public class Order
{
    public int Id { get; set; }

    /// <summary>Deliberately coarse — many rows share a date, so page boundaries land inside ties.</summary>
    public DateTime PlacedAt { get; set; }

    /// <summary>
    ///     Two decimal places against a column declared with four, so a boundary value that lost
    ///     precision on the way through a cursor would compare unequal to the row it came from.
    /// </summary>
    public decimal Total { get; set; }

    public OrderStatus Status { get; set; }

    public int CustomerId { get; set; }

    /// <summary>A string key, which orders through <c>IComparable</c> rather than an operator.</summary>
    public string Reference { get; set; } = "";

    /// <summary>Nullable, so it stands in for the column a keyset ordering refuses.</summary>
    public string? Note { get; set; }

    public Customer? Customer { get; set; }
}

/// <summary>Owns a collection, so a page over an <c>Include</c> can be exercised.</summary>
public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string? Email { get; set; }

    public List<Order> Orders { get; } = [];
}

/// <summary>
///     A <see cref="Guid" /> key and a <see cref="DateTimeOffset" /> column. Both are cases where the
///     database's ordering is not the CLR's — <c>uniqueidentifier</c> famously sorts by a byte order
///     of its own — which is fine for keyset paging only because the comparison and the
///     <c>ORDER BY</c> are evaluated by the same engine.
/// </summary>
public class Shipment
{
    public Guid Id { get; set; }

    public DateTimeOffset DispatchedAt { get; set; }

    public string Carrier { get; set; } = "";
}
