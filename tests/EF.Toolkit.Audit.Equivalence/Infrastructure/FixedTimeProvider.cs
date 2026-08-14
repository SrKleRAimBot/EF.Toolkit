namespace EFToolkit.Audit.Equivalence.Infrastructure;

/// <summary>
///     A clock that does not move.
/// </summary>
/// <remarks>
///     An audit entry's timestamp is part of what the harness compares, and two entries describing
///     the same change through different write paths are written at different moments. Pinning the
///     clock is what turns "these are equal apart from the time" into "these are equal", which is a
///     much cheaper assertion to trust.
/// </remarks>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    /// <summary>The instant every audit entry in the suite is stamped with.</summary>
    public static DateTimeOffset Instant { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A provider pinned to <see cref="Instant" />.</summary>
    public static FixedTimeProvider Default { get; } = new(Instant);

    public override DateTimeOffset GetUtcNow() => now;
}
