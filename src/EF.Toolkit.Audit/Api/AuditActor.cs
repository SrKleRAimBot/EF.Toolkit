namespace EFToolkit.Audit.Api;

/// <summary>
///     Who made a change.
/// </summary>
/// <param name="Id">
///     A stable identifier — a user id, a service principal, a job name. This is what an audit
///     query filters on, so it should not be a display name that can change.
/// </param>
/// <param name="Name">A human-readable name, recorded as it was at the time of the change.</param>
/// <param name="Type">
///     What kind of actor this is — <c>user</c>, <c>service</c>, <c>system</c>. Free-form, and
///     useful for telling a person's action apart from a background job's.
/// </param>
public readonly record struct AuditActor(string? Id, string? Name = null, string? Type = null)
{
    /// <summary>An actor that is not known.</summary>
    public static AuditActor Unknown => default;

    /// <summary>Whether this actor carries no identifying information at all.</summary>
    public bool IsUnknown => Id is null && Name is null;
}

/// <summary>
///     Supplies the actor for audit entries.
/// </summary>
/// <remarks>
///     Resolved from the <em>application</em> service provider, not EF Core's internal one, so an
///     implementation is free to depend on <c>IHttpContextAccessor</c> or anything else the
///     application registers.
/// </remarks>
public interface IAuditActorProvider
{
    /// <summary>Gets the actor responsible for the change currently being saved.</summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    ValueTask<AuditActor> GetActorAsync(CancellationToken cancellationToken);
}
