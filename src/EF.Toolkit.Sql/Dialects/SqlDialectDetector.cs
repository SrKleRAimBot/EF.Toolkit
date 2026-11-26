namespace EFToolkit.Sql.Dialects;

/// <summary>Maps an EF Core provider name to the dialect it speaks.</summary>
/// <remarks>
///     The provider name is the provider assembly's name, which is what both
///     <c>MigrationBuilder.ActiveProvider</c> and <c>DatabaseFacade.ProviderName</c> report. The match
///     is exact: a provider that merely resembles one of these (a fork, a wrapper) may speak a
///     different dialect, and guessing would be worse than asking.
/// </remarks>
internal static class SqlDialectDetector
{
    public const string PostgreSqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    public const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";
    public const string OracleMySqlProvider = "MySql.EntityFrameworkCore";
    public const string PomeloMySqlProvider = "Pomelo.EntityFrameworkCore.MySql";
    public const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    public const string KnownProviders =
        PostgreSqlProvider + ", " + SqlServerProvider + ", " + OracleMySqlProvider + ", "
        + PomeloMySqlProvider + ", " + SqliteProvider;

    /// <summary>The dialect for <paramref name="providerName" />.</summary>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not one this package knows.</exception>
    public static SqlDialect FromProviderName(string? providerName)
        => TryFromProviderName(providerName) ?? throw SqlFeatureNotSupportedException.UnknownProvider(providerName);

    /// <summary>The dialect for <paramref name="providerName" />, or <see langword="null" /> when unknown.</summary>
    public static SqlDialect? TryFromProviderName(string? providerName)
        => providerName switch
        {
            PostgreSqlProvider => SqlDialect.PostgreSql,
            SqlServerProvider => SqlDialect.SqlServer,
            OracleMySqlProvider or PomeloMySqlProvider => SqlDialect.MySql,
            SqliteProvider => SqlDialect.Sqlite,
            _ => null,
        };
}
