namespace EFToolkit.Conventions.Configuration;

/// <summary>Settings for EF.Toolkit.Conventions. Immutable; built by <see cref="ConventionsOptionsBuilder" />.</summary>
public sealed record ConventionsOptions
{
    /// <summary>The defaults.</summary>
    public static ConventionsOptions Default { get; } = new();

    /// <summary>The clock timestamps and soft-delete times are read from.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Whether a save turns deletes of soft-deletable entities into updates.</summary>
    public bool SoftDelete { get; init; } = true;

    /// <summary>Whether a save maintains created/updated timestamps.</summary>
    public bool Timestamps { get; init; } = true;
}
