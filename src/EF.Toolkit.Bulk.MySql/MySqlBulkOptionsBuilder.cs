using EFToolkit.Bulk.Configuration;

namespace EFToolkit.Bulk.MySql;

/// <summary>
///     MySQL-specific EF.Toolkit.Bulk settings.
/// </summary>
/// <remarks>
///     Exists so that MySQL-only knobs surface in <c>UseBulkOperations(...)</c> only when
///     EF.Toolkit.Bulk.MySql is installed. Provider-specific options are added here as the
///     corresponding execution paths land.
/// </remarks>
public class MySqlBulkOptionsBuilder : BulkOptionsBuilder
{
    /// <summary>Initializes a new builder seeded with <paramref name="options" />.</summary>
    /// <param name="options">The settings to start from.</param>
    public MySqlBulkOptionsBuilder(BulkOptions options)
        : base(options)
    {
    }
}
