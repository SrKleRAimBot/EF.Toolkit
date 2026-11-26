namespace EFToolkit.Testing;

/// <summary>Settings for <c>Database.ResetDataAsync()</c>.</summary>
public sealed class ResetDataOptions
{
    private readonly List<(string Name, string? Schema)> _excludedTables = [];
    private readonly List<Type> _excludedEntityTypes = [];

    /// <summary>
    ///     Whether identity columns, sequences and auto-increment counters restart, so that the next
    ///     row inserted gets the first value again. Default <see langword="true" />.
    /// </summary>
    public bool RestartIdentity { get; set; } = true;

    /// <summary>Leaves a table's rows in place.</summary>
    /// <param name="name">The table name, as mapped.</param>
    /// <param name="schema">
    ///     The table's schema. <see langword="null" /> matches the table of that name in any schema.
    /// </param>
    /// <returns>These options, for chaining.</returns>
    public ResetDataOptions ExcludeTable(string name, string? schema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _excludedTables.Add((name, schema));
        return this;
    }

    /// <summary>Leaves the rows of every table <typeparamref name="TEntity" /> maps to in place.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <returns>These options, for chaining.</returns>
    public ResetDataOptions ExcludeEntity<TEntity>()
        where TEntity : class
        => ExcludeEntity(typeof(TEntity));

    /// <summary>Leaves the rows of every table <paramref name="entityType" /> maps to in place.</summary>
    /// <param name="entityType">A mapped entity CLR type.</param>
    /// <returns>These options, for chaining.</returns>
    public ResetDataOptions ExcludeEntity(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        _excludedEntityTypes.Add(entityType);
        return this;
    }

    internal IReadOnlyList<(string Name, string? Schema)> ExcludedTables => _excludedTables;

    internal IReadOnlyList<Type> ExcludedEntityTypes => _excludedEntityTypes;

    internal bool HasExclusions => _excludedTables.Count > 0 || _excludedEntityTypes.Count > 0;

    internal static ResetDataOptions Build(Action<ResetDataOptions>? configure)
    {
        var options = new ResetDataOptions();
        configure?.Invoke(options);
        return options;
    }
}
