using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql;

/// <summary>
///     Thrown when a SQL feature has no faithful rendering on the database engine in use, or when
///     the engine itself is not one EF.Toolkit.Sql knows.
/// </summary>
/// <remarks>
///     The alternative to refusing is SQL that is subtly wrong for the engine — a view that is not
///     replaced, a policy that does not filter — which surfaces long after the migration that created
///     it. Every message names the feature and the engine, and where there is one, the way out.
/// </remarks>
public sealed class SqlFeatureNotSupportedException : NotSupportedException
{
    /// <summary>Initializes a new instance of the <see cref="SqlFeatureNotSupportedException" /> class.</summary>
    public SqlFeatureNotSupportedException()
        : base("The SQL feature is not supported by EF.Toolkit.Sql on this database engine.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SqlFeatureNotSupportedException" /> class.</summary>
    /// <param name="message">Describes what was refused and how to proceed instead.</param>
    public SqlFeatureNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SqlFeatureNotSupportedException" /> class.</summary>
    /// <param name="message">Describes what was refused and how to proceed instead.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SqlFeatureNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The feature that was refused, or <see langword="null" /> when the provider was unknown.</summary>
    public string? Feature { get; private init; }

    /// <summary>The engine the feature was refused on, or <see langword="null" /> when it was unknown.</summary>
    public SqlDialect? Dialect { get; private init; }

    /// <summary>The EF Core provider name that could not be mapped to a dialect, if that was the failure.</summary>
    public string? ProviderName { get; private init; }

    internal static SqlFeatureNotSupportedException For(
        string feature,
        SqlDialect dialect,
        string? alternative = null)
    {
        var message = $"{feature} is not supported on {SqlSyntax.EngineName(dialect)} by EF.Toolkit.Sql.";
        if (alternative is not null)
        {
            message += " " + alternative;
        }

        return new SqlFeatureNotSupportedException(message) { Feature = feature, Dialect = dialect };
    }

    internal static SqlFeatureNotSupportedException UnknownProvider(string? providerName)
        => new(
            providerName is null
                ? "No database provider is configured, so EF.Toolkit.Sql cannot choose a SQL dialect. "
                    + "Configure one of: " + SqlDialectDetector.KnownProviders + "."
                : $"The database provider '{providerName}' is not one EF.Toolkit.Sql knows the SQL "
                    + "dialect of, so it refuses to guess. Supported providers: "
                    + SqlDialectDetector.KnownProviders + ".")
        {
            ProviderName = providerName,
        };
}
