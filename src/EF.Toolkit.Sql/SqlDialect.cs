namespace EFToolkit.Sql;

/// <summary>The SQL dialects EF.Toolkit.Sql renders.</summary>
/// <remarks>
///     A dialect is chosen from the EF Core provider's name, never configured by hand: the provider
///     already decides what SQL the rest of the application speaks, and a second setting that could
///     disagree with it would only be a way to emit SQL for the wrong engine.
/// </remarks>
public enum SqlDialect
{
    /// <summary>PostgreSQL, through <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>.</summary>
    PostgreSql,

    /// <summary>SQL Server and Azure SQL, through <c>Microsoft.EntityFrameworkCore.SqlServer</c>.</summary>
    SqlServer,

    /// <summary>
    ///     MySQL, through either <c>MySql.EntityFrameworkCore</c> or
    ///     <c>Pomelo.EntityFrameworkCore.MySql</c>.
    /// </summary>
    MySql,

    /// <summary>SQLite, through <c>Microsoft.EntityFrameworkCore.Sqlite</c>.</summary>
    Sqlite,
}
