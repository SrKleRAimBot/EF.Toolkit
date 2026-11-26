namespace EFToolkit.Conventions.Configuration;

/// <summary>Configures EF.Toolkit.Conventions; passed to <c>UseEntityConventions(...)</c>.</summary>
public sealed class ConventionsOptionsBuilder
{
    internal ConventionsOptionsBuilder(ConventionsOptions options) => Options = options;

    internal ConventionsOptions Options { get; private set; }

    /// <summary>Reads times from <paramref name="clock" /> rather than the system clock.</summary>
    /// <param name="clock">The clock — a fake one in tests.</param>
    /// <returns>The same builder, for chaining.</returns>
    public ConventionsOptionsBuilder Clock(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Options = Options with { Clock = clock };
        return this;
    }

    /// <summary>
    ///     Switches the save-time soft delete off: <c>Remove()</c> deletes, while the query filter
    ///     the model declares still hides rows already marked.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ConventionsOptionsBuilder WithoutSoftDelete()
    {
        Options = Options with { SoftDelete = false };
        return this;
    }

    /// <summary>Switches timestamp maintenance off.</summary>
    /// <returns>The same builder, for chaining.</returns>
    public ConventionsOptionsBuilder WithoutTimestamps()
    {
        Options = Options with { Timestamps = false };
        return this;
    }
}
