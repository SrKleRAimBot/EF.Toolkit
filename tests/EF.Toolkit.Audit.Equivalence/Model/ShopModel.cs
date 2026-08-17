using EFToolkit.Audit.Api;

namespace EFToolkit.Audit.Equivalence.Model;

/// <summary>
///     An audited entity with an identity key, a converted enum, an excluded column and a masked one.
/// </summary>
/// <remarks>
///     The store-generated key is the point of it: an audit entry for an insert has to name the key
///     the database chose, which is not known when the values are captured. Everything else here
///     exists so that one entity type exercises conversion, exclusion and masking at once.
/// </remarks>
public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public string TenantId { get; set; } = "";
    public string? InternalNotes { get; set; }
    public string? CardNumber { get; set; }

    /// <summary>
    ///     A complex value, nested one level deeper.
    /// </summary>
    /// <remarks>
    ///     Its columns are on this table but are mapped by the complex type rather than by the
    ///     entity, so the two capture paths reach them differently: the change tracker reads them
    ///     off the entry, and the bulk path reads them off the columns the write staged. The
    ///     equivalence guarantee is that both produce the same entry regardless.
    /// </remarks>
    public Dimensions Dimensions { get; set; } = new();
}

/// <summary>A complex value object recorded under its path in the payload.</summary>
public class Dimensions
{
    public decimal Width { get; set; }
    public decimal Height { get; set; }

    public Packaging Packaging { get; set; } = new();
}

public class Packaging
{
    public string Material { get; set; } = "";
}

/// <summary>Stored as text, so the audit payload has to record the text and not the ordinal.</summary>
public enum ProductStatus
{
    Draft,
    Live,
    Retired,
}

/// <summary>
///     An audited entity with a client-generated key and an owned value object in the same table.
/// </summary>
/// <remarks>
///     Table splitting is the case worth having: EF tracks <see cref="Address" /> as an entry of its
///     own, so a change to it alone leaves the owner Unchanged and would go unrecorded by anything
///     that only looks at the owner's state.
/// </remarks>
public class Warehouse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Address Address { get; set; } = new();
    public string TenantId { get; set; } = "";
}

/// <summary>An owned value object, mapped into its owner's table.</summary>
public class Address
{
    public string City { get; set; } = "";
    public string Postcode { get; set; } = "";
}

/// <summary>An audited entity with a composite key, so key rendering has to escape.</summary>
public class Assignment
{
    public string ProductSku { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "";
}

/// <summary>Registered with an attribute rather than fluently, and masked the same way.</summary>
[Audited]
public class ApiKey
{
    public int Id { get; set; }

    [AuditMask]
    public string Secret { get; set; } = "";

    [AuditIgnore]
    public string? Scratch { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>
///     An audited entity whose properties are declared as CLR types the drivers do not hand back
///     for those columns by default.
/// </summary>
/// <remarks>
///     <para>
///         A before-image is read straight off the reader, so a column whose driver default type is
///         not the property's declared type has to be asked for by type rather than reconciled
///         afterwards — most of these pairs have no conversion between them at all.
///     </para>
///     <para>
///         One entity covers both engines, because they disagree about which columns those are.
///         Npgsql reads <c>timestamptz</c> as a <see cref="DateTime" />, so
///         <see cref="RecordedAt" /> is the mismatched one there; SQL Server reads <c>date</c> as a
///         <see cref="DateTime" /> and <c>time</c> as a <see cref="TimeSpan" />, so
///         <see cref="Date" /> and <see cref="StartsAt" /> are the mismatched ones there.
///     </para>
/// </remarks>
public class Shift
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Read as a <see cref="DateTime" /> by SQL Server.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Read as a <see cref="TimeSpan" /> by SQL Server.</summary>
    public TimeOnly StartsAt { get; set; }

    /// <summary>Read as a <see cref="DateTime" /> by Npgsql.</summary>
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>Not audited, so that "everything is audited" is never accidentally true.</summary>
public class Session
{
    public int Id { get; set; }
    public string Token { get; set; } = "";
}
