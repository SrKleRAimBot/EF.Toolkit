namespace EFToolkit.Query.Tests.Infrastructure;

/// <summary>The status of an <see cref="Order" />. Exercises enum keys and value conversion.</summary>
public enum OrderStatus
{
    Placed = 0,
    Shipped = 1,
    Cancelled = 2,
}

/// <summary>The main subject: a plain entity with an integer key and several sortable columns.</summary>
public class Order
{
    public int Id { get; set; }

    public DateTime PlacedAt { get; set; }

    public decimal Total { get; set; }

    public OrderStatus Status { get; set; }

    public int CustomerId { get; set; }

    public string Reference { get; set; } = "";

    /// <summary>Optional, so it can stand in for the nullable column a keyset ordering refuses.</summary>
    public string? Note { get; set; }

    public Customer? Customer { get; set; }
}

/// <summary>Owns a collection, so the collection-include advisory has something to find.</summary>
public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string? Email { get; set; }

    public List<Order> Orders { get; } = [];
}

/// <summary>A Guid key, which orders through <c>IComparable</c> rather than an operator.</summary>
public class Shipment
{
    public Guid Id { get; set; }

    public DateTimeOffset DispatchedAt { get; set; }
}

/// <summary>Not mapped, so it stands in for a projection that the model knows nothing about.</summary>
public class OrderSummary
{
    public int Id { get; set; }

    public decimal Total { get; set; }
}
