using EFToolkit.Bulk.Configuration;

namespace EFToolkit.Bulk.PostgreSQL;

/// <summary>
///     PostgreSQL-specific EF.Toolkit.Bulk settings.
/// </summary>
/// <remarks>
///     Exists so that PostgreSQL-only knobs surface in <c>UseBulkOperations(...)</c> only when
///     EF.Toolkit.Bulk.PostgreSQL is installed. Provider-specific options are added here as the
///     corresponding execution paths land.
/// </remarks>
public class NpgsqlBulkOptionsBuilder : BulkOptionsBuilder
{
    /// <summary>Initializes a new builder seeded with <paramref name="options" />.</summary>
    /// <param name="options">The settings to start from.</param>
    public NpgsqlBulkOptionsBuilder(BulkOptions options)
        : base(options)
    {
    }
}
