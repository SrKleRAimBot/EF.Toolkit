using EFToolkit.Audit.Api;

namespace EFToolkit.Audit.Tests.Infrastructure;

/// <summary>An ordinary entity, registered fluently in the tests that need it.</summary>
public class Order
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    public string? CardNumber { get; set; }
    public string? InternalNotes { get; set; }
    public string TenantId { get; set; } = "";
}

/// <summary>Registered by attribute, with a masked and an ignored property.</summary>
[Audited]
public class Credential
{
    public int Id { get; set; }

    [AuditMask]
    public string Secret { get; set; } = "";

    [AuditIgnore]
    public string? Scratch { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>Excluded by attribute, for the opt-out default.</summary>
[NotAudited]
public class Telemetry
{
    public int Id { get; set; }
    public string Payload { get; set; } = "";
}

/// <summary>Registered nowhere.</summary>
public class Anonymous
{
    public int Id { get; set; }
    public string Value { get; set; } = "";
}

/// <summary>A composite key, for key rendering.</summary>
public class Membership
{
    public string GroupId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "";
}
