using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Infrastructure;

/// <summary>
///     Names for the runtime annotations this library hangs off EF's metadata objects.
/// </summary>
/// <remarks>
///     <para>
///         Everything derived from the model — compiled accessors, the per-state column plan, the
///         topological order of the entity types — is expensive to build and can never change once
///         the model is finalized, so all of it is cached. The obvious place is a static dictionary
///         keyed by the metadata object, and that is what these were. It works, and it makes the
///         cache a GC root for every model the process has ever built: a test suite, or a host with
///         a model per tenant, then holds every model it has ever seen until the process exits,
///         along with each one's compiled delegates.
///     </para>
///     <para>
///         EF's runtime annotations are the answer to exactly this. The derived value lives on the
///         metadata object it was derived from, so it is collected with the model rather than
///         outliving it, the lookup is still a dictionary hit, and the value is still built once.
///         It is what EF itself does with what it derives from a finalized model, and it is
///         available here for the same reason: everything this library caches is keyed off metadata
///         that is already read-only by the time it arrives.
///     </para>
/// </remarks>
internal static class BulkAnnotations
{
    // Runtime annotations share one namespace per metadata object with EF's own and with every
    // other library's, so the prefix is not decoration.
    private const string Prefix = "EFToolkit.Bulk:";

    /// <summary>Compiled getter for a property. Set on the <c>IProperty</c>.</summary>
    public const string Getter = Prefix + "Getter";

    /// <summary>
    ///     Compiled setter for a property, or <see langword="null" /> when the property has no
    ///     writable member. Set on the <c>IProperty</c>.
    /// </summary>
    public const string Setter = Prefix + "Setter";

    /// <summary>Navigation and foreign-key fixup plan. Set on the <c>IEntityType</c>.</summary>
    public const string GraphPlan = Prefix + "GraphPlan";

    /// <summary>Entity types ordered principals-first. Set on the <c>IModel</c>.</summary>
    public const string TopologicalOrder = Prefix + "TopologicalOrder";

    /// <summary>
    ///     The column plan for one entity state. Set on the <c>IEntityType</c>.
    /// </summary>
    /// <remarks>
    ///     A state with no bulk equivalent gets a name of its own rather than an exception here, so
    ///     that <c>BulkEntityPlan.Build</c> keeps sole ownership of that message — it can name the
    ///     entity type, and this cannot. Nothing is ever stored under it: a factory that throws
    ///     adds no annotation.
    /// </remarks>
    public static string Plan(EntityState state)
        => state switch
        {
            EntityState.Added => Prefix + "Plan:Added",
            EntityState.Modified => Prefix + "Plan:Modified",
            EntityState.Deleted => Prefix + "Plan:Deleted",
            _ => Prefix + "Plan:Unsupported",
        };
}
