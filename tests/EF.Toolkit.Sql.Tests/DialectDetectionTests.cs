using EFToolkit.Sql.Dialects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Sql.Tests;

public sealed class DialectDetectionTests
{
    [Theory]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", SqlDialect.PostgreSql)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", SqlDialect.SqlServer)]
    [InlineData("MySql.EntityFrameworkCore", SqlDialect.MySql)]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", SqlDialect.MySql)]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", SqlDialect.Sqlite)]
    public void Known_provider_names_map_to_their_dialect(string provider, SqlDialect expected)
        => SqlDialectDetector.FromProviderName(provider).ShouldBe(expected);

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.InMemory")]
    [InlineData("Microsoft.EntityFrameworkCore.Cosmos")]
    [InlineData("npgsql.entityframeworkcore.postgresql")]
    [InlineData("")]
    public void An_unknown_provider_is_refused_by_name(string provider)
    {
        var ex = Should.Throw<SqlFeatureNotSupportedException>(() => SqlDialectDetector.FromProviderName(provider));

        ex.ProviderName.ShouldBe(provider);
        ex.Feature.ShouldBeNull();
        ex.Dialect.ShouldBeNull();
        ex.Message.ShouldContain($"'{provider}'");
        ex.Message.ShouldContain("Npgsql.EntityFrameworkCore.PostgreSQL");
        SqlDialectDetector.TryFromProviderName(provider).ShouldBeNull();
    }

    [Fact]
    public void No_provider_at_all_is_refused()
    {
        var ex = Should.Throw<SqlFeatureNotSupportedException>(() => SqlDialectDetector.FromProviderName(null));
        ex.Message.ShouldContain("No database provider");
    }

    [Fact]
    public void The_facade_reports_the_dialect_of_real_providers()
    {
        using var npgsql = new DbContext(new DbContextOptionsBuilder().UseNpgsql("Host=unused").Options);
        using var sqlServer = new DbContext(new DbContextOptionsBuilder().UseSqlServer("Server=unused").Options);
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var sqlite = new DbContext(new DbContextOptionsBuilder().UseSqlite(connection).Options);

        npgsql.Database.GetSqlDialect().ShouldBe(SqlDialect.PostgreSql);
        sqlServer.Database.GetSqlDialect().ShouldBe(SqlDialect.SqlServer);
        sqlite.Database.GetSqlDialect().ShouldBe(SqlDialect.Sqlite);
    }

    [Fact]
    public void Unsupported_feature_errors_name_the_feature_and_the_engine()
    {
        var ex = SqlFeatureNotSupportedException.For("Widgets", SqlDialect.MySql, "Use gadgets.");

        ex.Feature.ShouldBe("Widgets");
        ex.Dialect.ShouldBe(SqlDialect.MySql);
        ex.Message.ShouldBe("Widgets is not supported on MySQL by EF.Toolkit.Sql. Use gadgets.");
        ex.ShouldBeAssignableTo<NotSupportedException>();
    }

    [Fact]
    public void The_exception_has_the_standard_constructors()
    {
        new SqlFeatureNotSupportedException().Message.ShouldNotBeNullOrWhiteSpace();
        new SqlFeatureNotSupportedException("m").Message.ShouldBe("m");
        var inner = new InvalidOperationException();
        new SqlFeatureNotSupportedException("m", inner).InnerException.ShouldBeSameAs(inner);
    }
}
