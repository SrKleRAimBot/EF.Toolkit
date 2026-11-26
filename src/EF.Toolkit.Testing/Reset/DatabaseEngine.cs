namespace EFToolkit.Testing;

/// <summary>The engines a data reset knows how to empty.</summary>
internal enum DatabaseEngine
{
    PostgreSql,
    SqlServer,
    MySql,
    Sqlite,
}

/// <summary>Maps an EF Core provider to the engine behind it.</summary>
internal static class DatabaseEngines
{
    /// <summary>Returns the engine for <paramref name="providerName" />.</summary>
    /// <param name="providerName">The value of <c>Database.ProviderName</c>.</param>
    /// <exception cref="NotSupportedException">The provider is not one a reset supports.</exception>
    /// <remarks>
    ///     Read off the provider's assembly name rather than its option extensions so that neither
    ///     provider is a dependency of this package — the MySQL providers in particular are two
    ///     unrelated codebases that only agree on the engine.
    /// </remarks>
    public static DatabaseEngine Detect(string? providerName)
        => providerName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" => DatabaseEngine.PostgreSql,
            "Microsoft.EntityFrameworkCore.SqlServer" => DatabaseEngine.SqlServer,
            "MySql.EntityFrameworkCore" or "Pomelo.EntityFrameworkCore.MySql" => DatabaseEngine.MySql,
            "Microsoft.EntityFrameworkCore.Sqlite" => DatabaseEngine.Sqlite,
            _ => throw new NotSupportedException(
                $"Resetting data is not supported for the EF Core provider '{providerName ?? "(none)"}'. "
                + "Supported providers are Npgsql.EntityFrameworkCore.PostgreSQL, "
                + "Microsoft.EntityFrameworkCore.SqlServer, MySql.EntityFrameworkCore, "
                + "Pomelo.EntityFrameworkCore.MySql and Microsoft.EntityFrameworkCore.Sqlite."),
        };
}
