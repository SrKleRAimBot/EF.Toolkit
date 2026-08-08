using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace EFBulk.Planning;

/// <summary>
///     A run of modification commands that target the same table, in the same state, writing the
///     same columns — the unit that can be handed to a bulk copy as one operation.
/// </summary>
public sealed class BulkPartition
{
    internal BulkPartition(
        string? schema,
        string tableName,
        EntityState entityState,
        IReadOnlyList<IReadOnlyModificationCommand> commands,
        string? ineligibleReason,
        bool belowThreshold)
    {
        Schema = schema;
        TableName = tableName;
        EntityState = entityState;
        Commands = commands;
        IneligibleReason = ineligibleReason;
        BelowThreshold = belowThreshold;
    }

    /// <summary>Schema of the target table, or <see langword="null" /> for the default schema.</summary>
    public string? Schema { get; }

    /// <summary>Name of the target table.</summary>
    public string TableName { get; }

    /// <summary>Whether these rows are being inserted, updated or deleted.</summary>
    public EntityState EntityState { get; }

    /// <summary>The commands in this partition, all of identical shape.</summary>
    public IReadOnlyList<IReadOnlyModificationCommand> Commands { get; }

    /// <summary>
    ///     Why this partition's <em>shape</em> cannot be accelerated, or <see langword="null" /> if
    ///     it can. Distinct from <see cref="BelowThreshold" />: a shape EF.Bulk does not support is
    ///     worth reporting under <see cref="Configuration.Unsupported.Throw" />, whereas a small
    ///     partition is simply not worth accelerating.
    /// </summary>
    public string? IneligibleReason { get; }

    /// <summary>Whether this partition is too small for bulk execution to pay for itself.</summary>
    public bool BelowThreshold { get; }

    /// <summary>Whether this partition should be executed as a bulk operation.</summary>
    public bool CanAccelerate => IneligibleReason is null && !BelowThreshold;

    /// <summary>A short description used in diagnostics and exception messages.</summary>
    public override string ToString()
    {
        var table = Schema is null ? TableName : $"{Schema}.{TableName}";
        return $"{EntityState} × {Commands.Count} on {table}";
    }
}
