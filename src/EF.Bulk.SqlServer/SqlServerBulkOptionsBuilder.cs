using EFBulk.Configuration;

namespace EFBulk.SqlServer;

/// <summary>
///     SQL Server-specific EF.Bulk settings.
/// </summary>
/// <remarks>
///     Exists so that SQL Server-only knobs surface in <c>UseBulkOperations(...)</c> only when
///     EF.Bulk.SqlServer is installed. Provider-specific options are added here as the
///     corresponding execution paths land.
/// </remarks>
public class SqlServerBulkOptionsBuilder : BulkOptionsBuilder
{
    /// <summary>Initializes a new builder seeded with <paramref name="options" />.</summary>
    /// <param name="options">The settings to start from.</param>
    public SqlServerBulkOptionsBuilder(BulkOptions options)
        : base(options)
    {
    }
}
