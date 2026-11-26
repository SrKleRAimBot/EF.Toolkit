using System.Data;
using EFToolkit.Sql.Execution;
using Microsoft.Data.SqlClient;

namespace EFToolkit.Sql.Tests;

public sealed class RoutineCallSqlTests
{
    [Fact]
    public void PostgreSql_calls_with_named_notation()
    {
        var (sql, args) = RoutineCallSql.Procedure(SqlDialect.PostgreSql, "close_month", "billing", new { tenant_id = 7, note = "x" });

        sql.ShouldBe("CALL \"billing\".\"close_month\"(tenant_id => {0}, note => {1})");
        args.ShouldBe([7, "x"]);
    }

    [Fact]
    public void PostgreSql_quotes_a_reserved_argument_name_and_leaves_others_to_fold()
    {
        // `from => ...` does not parse; `"from" => ...` does. A mixed-case name stays bare, folding to the
        // lowercase name an unquoted parameter of the routine has.
        var (sql, _) = RoutineCallSql.Procedure(SqlDialect.PostgreSql, "report", null, new { From = 1, tenantId = 2 });

        sql.ShouldBe("CALL \"report\"(\"from\" => {0}, tenantId => {1})");
    }

    [Fact]
    public void SqlServer_execs_with_named_parameters_and_marks_output_ones()
    {
        var total = new SqlParameter("@total", SqlDbType.Int) { Direction = ParameterDirection.Output };
        var counter = new SqlParameter("@counter", SqlDbType.Int) { Direction = ParameterDirection.InputOutput, Value = 1 };

        var (sql, args) = RoutineCallSql.Procedure(SqlDialect.SqlServer, "close_month", null, new { tenant_id = 7, total, counter });

        sql.ShouldBe("EXEC [close_month] @tenant_id = {0}, @total = {1} OUTPUT, @counter = {2} OUTPUT");
        args.ShouldBe([7, total, counter]);
    }

    [Fact]
    public void SqlServer_without_arguments()
        => RoutineCallSql.Procedure(SqlDialect.SqlServer, "p", "dbo", null).Sql.ShouldBe("EXEC [dbo].[p]");

    [Fact]
    public void MySql_calls_by_position_in_the_order_given()
    {
        var (sql, args) = RoutineCallSql.Procedure(SqlDialect.MySql, "p", null, new { b = 2, a = 1 });

        sql.ShouldBe("CALL `p`({0}, {1})");
        args.ShouldBe([2, 1]);
    }

    [Fact]
    public void Sqlite_cannot_call_procedures()
        => Should.Throw<SqlFeatureNotSupportedException>(() => RoutineCallSql.Procedure(SqlDialect.Sqlite, "p", null, null))
            .Feature.ShouldBe("Calling stored procedures");

    [Fact]
    public void PostgreSql_functions_are_selected_from_with_named_notation()
    {
        var (sql, args) = RoutineCallSql.Function(SqlDialect.PostgreSql, "orders_for", null, new Dictionary<string, object?> { ["tenant"] = 3 });

        sql.ShouldBe("SELECT * FROM \"orders_for\"(tenant => {0})");
        args.ShouldBe([3]);
    }

    [Fact]
    public void SqlServer_functions_take_positional_arguments()
        => RoutineCallSql.Function(SqlDialect.SqlServer, "orders_for", "dbo", new { tenant = 3, since = 1 })
            .Sql.ShouldBe("SELECT * FROM [dbo].[orders_for]({0}, {1})");

    [Theory]
    [InlineData(SqlDialect.MySql, "Table-valued functions")]
    [InlineData(SqlDialect.Sqlite, "Querying table-valued functions")]
    public void Engines_without_table_functions_refuse(SqlDialect dialect, string feature)
        => Should.Throw<SqlFeatureNotSupportedException>(() => RoutineCallSql.Function(dialect, "f", null, null))
            .Feature.ShouldBe(feature);

    [Fact]
    public void Braces_in_names_survive_EF_formatting()
    {
        var (sql, _) = RoutineCallSql.Procedure(SqlDialect.PostgreSql, "we{ird}", null, new { a = 1 });

        sql.ShouldBe("CALL \"we{{ird}}\"(a => {0})");
        string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, "@p0").ShouldBe("CALL \"we{ird}\"(a => @p0)");
    }

    [Fact]
    public void Undefined_dialects_are_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => RoutineCallSql.Procedure((SqlDialect)42, "p", null, null));
        Should.Throw<ArgumentOutOfRangeException>(() => RoutineCallSql.Function((SqlDialect)42, "p", null, null));
    }
}

public sealed class RoutineArgumentsTests
{
    [Fact]
    public void Null_means_no_arguments()
        => RoutineArguments.From(null).ShouldBeEmpty();

    [Fact]
    public void Anonymous_objects_keep_declaration_order()
        => RoutineArguments.From(new { zeta = 1, alpha = "a", mid = (object?)null })
            .ShouldBe([("zeta", (object?)1), ("alpha", "a"), ("mid", null)]);

    [Fact]
    public void Dictionaries_keep_insertion_order_and_drop_an_at_prefix()
        => RoutineArguments.From(new Dictionary<string, object?> { ["@b"] = 2, ["a"] = 1 })
            .ShouldBe([("b", (object?)2), ("a", 1)]);

    [Fact]
    public void Mutable_dictionaries_work_through_their_pairs()
        => RoutineArguments.From(new SortedList<string, object?> { ["a"] = 1 }).ShouldBe([("a", (object?)1)]);

    [Fact]
    public void Plain_classes_contribute_their_public_readable_properties()
        => RoutineArguments.From(new Args { Tenant = 4 }).ShouldBe([("Tenant", (object?)4)]);

    [Fact]
    public void DbParameter_values_pass_through()
    {
        var parameter = new SqlParameter("@x", 1);
        RoutineArguments.From(new { x = parameter }).Single().Value.ShouldBeSameAs(parameter);
    }

    [Theory]
    [MemberData(nameof(Unnameable))]
    public void Values_that_cannot_name_themselves_are_refused(object args)
        => Should.Throw<ArgumentException>(() => RoutineArguments.From(args));

    public static TheoryData<object> Unnameable() => new() { "text", new object[] { 1, 2 }, new List<int> { 1 }, new SqlParameter("@a", 1) };

    [Fact]
    public void Names_that_cannot_appear_bare_are_refused()
        => Should.Throw<ArgumentException>(() => RoutineArguments.From(new Dictionary<string, object?> { ["a; DROP"] = 1 }));

    [Fact]
    public void Duplicate_names_are_refused_ignoring_case_and_prefix()
        => Should.Throw<ArgumentException>(() => RoutineArguments.From(new Dictionary<string, object?> { ["a"] = 1, ["@A"] = 2 }));

    private sealed class Args
    {
        public int Tenant { get; init; }

        public int this[int i] => i;

        internal int Hidden { get; init; }

        public static int Static => 0;

#pragma warning disable CA1822, S1144 // Write-only properties are what is under test.
        public int WriteOnly { set { _ = value; } }
#pragma warning restore CA1822, S1144
    }
}
