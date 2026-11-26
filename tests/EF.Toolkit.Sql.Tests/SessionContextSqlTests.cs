using System.Globalization;
using EFToolkit.Sql.Sessions;
using Microsoft.Data.Sqlite;

namespace EFToolkit.Sql.Tests;

public sealed class SessionContextSqlTests
{
    [Fact]
    public void PostgreSql_binds_key_and_value_through_set_config()
    {
        var (sql, args) = SessionContextSql.ForRawSql(SqlDialect.PostgreSql, "app.tenant_id", 42);

        sql.ShouldBe("SELECT set_config({0}, {1}, false)");
        args.ShouldBe(["app.tenant_id", "42"]);
    }

    [Fact]
    public void SqlServer_binds_key_and_value_through_sp_set_session_context()
    {
        var (sql, args) = SessionContextSql.ForRawSql(SqlDialect.SqlServer, "TenantId", 42);

        sql.ShouldBe("EXEC sp_set_session_context @key = {0}, @value = {1}");
        args.ShouldBe(["TenantId", 42]);
    }

    [Fact]
    public void MySql_writes_the_validated_variable_name_and_binds_the_value()
    {
        var (sql, args) = SessionContextSql.ForRawSql(SqlDialect.MySql, "app.tenant_id", 42);

        sql.ShouldBe("SET @app.tenant_id = {0}");
        args.ShouldBe([42]);
    }

    [Fact]
    public void A_null_stays_null_for_EF_raw_sql_which_refuses_DBNull()
        => SessionContextSql.ForRawSql(SqlDialect.SqlServer, "TenantId", null).Arguments.ShouldBe(["TenantId", null]);

    [Fact]
    public void Sqlite_has_no_session_context()
    {
        var ex = Should.Throw<SqlFeatureNotSupportedException>(() => SessionContextSql.ForRawSql(SqlDialect.Sqlite, "k", 1));
        ex.Feature.ShouldBe("Session context");
        ex.Dialect.ShouldBe(SqlDialect.Sqlite);
    }

    [Theory]
    [InlineData(SqlDialect.PostgreSql, "tenant_id")]
    [InlineData(SqlDialect.PostgreSql, "app.tenant id")]
    [InlineData(SqlDialect.PostgreSql, ".tenant")]
    [InlineData(SqlDialect.MySql, "x; DROP TABLE t")]
    [InlineData(SqlDialect.MySql, "a-b")]
    [InlineData(SqlDialect.PostgreSql, " ")]
    public void Keys_the_engine_would_reject_are_refused(SqlDialect dialect, string key)
        => Should.Throw<ArgumentException>(() => SessionContextSql.ValidateKey(dialect, key));

    [Theory]
    [InlineData(SqlDialect.PostgreSql, "app.tenant_id")]
    [InlineData(SqlDialect.PostgreSql, "my_app.a.b")]
    [InlineData(SqlDialect.SqlServer, "Any key, really")]
    [InlineData(SqlDialect.MySql, "app.tenant$1")]
    public void Valid_keys_are_accepted(SqlDialect dialect, string key)
        => SessionContextSql.ValidateKey(dialect, key);

    [Fact]
    public void Undefined_dialects_are_refused()
        => Should.Throw<ArgumentOutOfRangeException>(() => SessionContextSql.ValidateKey((SqlDialect)42, "k"));

    [Fact]
    public void A_command_sets_every_value_in_one_round_trip()
    {
        var (sql, parameters) = SessionContextSql.ForCommand(
            SqlDialect.PostgreSql,
            [("app.tenant_id", 1), ("app.user_id", "u")]);

        sql.ShouldBe(
            "SELECT set_config(@ef_toolkit_key0, @ef_toolkit_value0, false);\n"
            + "SELECT set_config(@ef_toolkit_key1, @ef_toolkit_value1, false)");
        parameters.ShouldBe(
        [
            ("@ef_toolkit_key0", (object)"app.tenant_id"),
            ("@ef_toolkit_value0", "1"),
            ("@ef_toolkit_key1", "app.user_id"),
            ("@ef_toolkit_value1", "u"),
        ]);
    }

    [Fact]
    public void A_SqlServer_command()
    {
        var (sql, parameters) = SessionContextSql.ForCommand(SqlDialect.SqlServer, [("TenantId", null)]);

        sql.ShouldBe("EXEC sp_set_session_context @key = @ef_toolkit_key0, @value = @ef_toolkit_value0");
        parameters.ShouldBe([("@ef_toolkit_key0", (object)"TenantId"), ("@ef_toolkit_value0", DBNull.Value)]);
    }

    [Fact]
    public void A_MySql_command_binds_only_values()
    {
        var (sql, parameters) = SessionContextSql.ForCommand(SqlDialect.MySql, [("a", 1), ("b", 2)]);

        sql.ShouldBe("SET @a = @ef_toolkit_value0;\nSET @b = @ef_toolkit_value1");
        parameters.ShouldBe([("@ef_toolkit_value0", (object)1), ("@ef_toolkit_value1", 2)]);
    }

    [Fact]
    public void The_command_is_created_on_the_connection_with_its_parameters()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");

        using var command = SessionContextSql.CreateCommand(connection, SqlDialect.SqlServer, [("TenantId", 5)]);

        command.Connection.ShouldBeSameAs(connection);
        command.CommandText.ShouldBe("EXEC sp_set_session_context @key = @ef_toolkit_key0, @value = @ef_toolkit_value0");
        command.Parameters.Count.ShouldBe(2);
        command.Parameters[1].ParameterName.ShouldBe("@ef_toolkit_value0");
        command.Parameters[1].Value.ShouldBe(5);
    }

    [Fact]
    public void A_SQL_Server_DateTime_is_bound_as_datetime2_so_it_is_not_rounded()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var at = new DateTime(2026, 11, 26, 12, 30, 0).AddMilliseconds(1);

        using var command = SessionContextSql.CreateCommand(connection, SqlDialect.SqlServer, [("TenantAt", at)]);

        command.Parameters[1].DbType.ShouldBe(System.Data.DbType.DateTime2);
    }

    [Fact]
    public void PostgreSql_values_are_text_formatted_invariantly()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            SessionContextSql.ConvertValue(SqlDialect.PostgreSql, 1.5m).ShouldBe("1.5");
            SessionContextSql.ConvertValue(SqlDialect.PostgreSql, new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc))
                .ShouldBe("2026-03-04T05:06:07.0000000Z");
            SessionContextSql.ConvertValue(SqlDialect.PostgreSql, new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2)))
                .ShouldBe("2026-03-04T05:06:07.0000000+02:00");

            // Not 03/04/2026, which DateStyle DMY reads as April 3, nor 12:30 without its seconds.
            SessionContextSql.ConvertValue(SqlDialect.PostgreSql, new DateOnly(2026, 3, 4)).ShouldBe("2026-03-04");
            SessionContextSql.ConvertValue(SqlDialect.PostgreSql, new TimeOnly(12, 30, 45)).ShouldBe("12:30:45.0000000");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var id = Guid.NewGuid();
        SessionContextSql.ConvertValue(SqlDialect.PostgreSql, id).ShouldBe(id.ToString());
        SessionContextSql.ConvertValue(SqlDialect.PostgreSql, true).ShouldBe("true");
        SessionContextSql.ConvertValue(SqlDialect.PostgreSql, false).ShouldBe("false");
        SessionContextSql.ConvertValue(SqlDialect.PostgreSql, null).ShouldBe("");
        SessionContextSql.ConvertValue(SqlDialect.PostgreSql, DBNull.Value).ShouldBe("");
        SessionContextSql.ConvertValue(SqlDialect.PostgreSql, "s").ShouldBe("s");
        SessionContextSql.ConvertValue(SqlDialect.PostgreSql, new Custom()).ShouldBe("custom");
    }

    [Fact]
    public void Other_engines_keep_the_value_typed()
    {
        SessionContextSql.ConvertValue(SqlDialect.SqlServer, 5).ShouldBe(5);
        SessionContextSql.ConvertValue(SqlDialect.SqlServer, null).ShouldBeNull();
        SessionContextSql.ConvertValue(SqlDialect.SqlServer, DBNull.Value).ShouldBeNull();
        SessionContextSql.ConvertValue(SqlDialect.MySql, 5L).ShouldBe(5L);
    }

    private sealed class Custom
    {
        public override string ToString() => "custom";
    }
}
