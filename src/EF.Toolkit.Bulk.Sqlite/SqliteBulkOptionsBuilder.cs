using EFToolkit.Bulk.Configuration;

namespace EFToolkit.Bulk.Sqlite;

/// <summary>
///     SQLite-specific EF.Toolkit.Bulk settings.
/// </summary>
/// <remarks>
///     Exists so that SQLite-only knobs surface in <c>UseBulkOperations(...)</c> only when
///     EF.Toolkit.Bulk.Sqlite is installed. Provider-specific options are added here as the
///     corresponding execution paths land.
/// </remarks>
public class SqliteBulkOptionsBuilder : BulkOptionsBuilder
{
    /// <summary>Initializes a new builder seeded with <paramref name="options" />.</summary>
    /// <param name="options">The settings to start from.</param>
    public SqliteBulkOptionsBuilder(BulkOptions options)
        : base(options)
    {
    }
}
