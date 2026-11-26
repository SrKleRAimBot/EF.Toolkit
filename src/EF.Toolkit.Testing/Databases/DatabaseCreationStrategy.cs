namespace EFToolkit.Testing;

/// <summary>How a test database gets its schema.</summary>
public enum DatabaseCreationStrategy
{
    /// <summary>
    ///     <c>Database.EnsureCreated()</c>: the schema straight from the model. Fast, and right unless
    ///     the migrations themselves are under test. The default.
    /// </summary>
    EnsureCreated,

    /// <summary>
    ///     <c>Database.Migrate()</c>: the schema the application's migrations produce, including any
    ///     hand-written SQL in them. Slower, but it is the schema production runs on.
    /// </summary>
    Migrate,

    /// <summary>Nothing: the schema already exists — created by a container init script, say.</summary>
    None,
}
