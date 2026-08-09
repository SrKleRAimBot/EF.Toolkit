namespace EFToolkit.Bulk.Equivalence.Model;

/// <summary>
///     A three-level foreign-key chain: <see cref="Customer" /> → <see cref="Order" /> →
///     <see cref="OrderLine" />. Exercises the ordering problem that makes bulk writes hard.
/// </summary>
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public List<Order> Orders { get; } = [];
}

public class Order
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>Nullable foreign key — exercises the "principal may be absent" path.</summary>
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    public string Reference { get; set; } = "";

    /// <summary>
    ///     Stored as a string via a value converter. Bulk copy writers bypass EF's parameter
    ///     construction, so they must apply <c>ConvertToProvider</c> themselves — this catches it
    ///     if they do not.
    /// </summary>
    public OrderStatus Status { get; set; }

    public DateTime PlacedAt { get; set; }

    public List<OrderLine> Lines { get; } = [];
    public List<OrderNote> Notes { get; } = [];
}

public class OrderLine
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

/// <summary>
///     Client-generated key, so nothing has to be read back after insert. This is the shape that
///     reaches the fastest path — a straight bulk copy with no staging table and no correlation —
///     and it is a child of an identity-keyed parent, so a single save still has to order the two.
/// </summary>
public class OrderNote
{
    public Guid Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public string Text { get; set; } = "";
}

/// <summary>
///     Carries a client-managed concurrency token. Its column is both written and used to locate
///     the row, so a staged update has to carry the loaded value and the new value at once.
/// </summary>
public class Inventory
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }

    /// <summary>Incremented by the application on every write.</summary>
    public int Version { get; set; }
}

/// <summary>
///     Self-referencing table. Rows within a single insert can depend on each other, so this is the
///     case where table-level ordering is not enough and row-level layering is required.
/// </summary>
public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    public int? ParentId { get; set; }
    public Category? Parent { get; set; }

    public List<Category> Children { get; } = [];
}

public enum OrderStatus
{
    Draft,
    Placed,
    Shipped,
    Cancelled
}
