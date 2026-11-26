using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing.Tests;

public sealed class ResetPlanTests
{
    private static ResetPlan Plan(DbContext context, Action<ResetDataOptions>? configure = null)
        => ResetPlan.Build(context.Model, ResetDataOptions.Build(configure));

    private static List<string> Names(ResetPlan plan) => [.. plan.Tables.Select(t => t.Name)];

    [Fact]
    public void Every_mapped_table_is_planned_and_views_and_queries_are_not()
    {
        using var context = Contexts.Sqlite();

        Names(Plan(context)).Order().ShouldBe(
            ["Categories", "Customers", "OrderLines", "Orders", "Players", "Products", "Tags", "Teams"]);
    }

    [Fact]
    public void Dependents_come_before_their_principals()
    {
        using var context = Contexts.Sqlite();
        var names = Names(Plan(context));

        names.IndexOf("OrderLines").ShouldBeLessThan(names.IndexOf("Orders"));
        names.IndexOf("OrderLines").ShouldBeLessThan(names.IndexOf("Products"));
        names.IndexOf("Orders").ShouldBeLessThan(names.IndexOf("Customers"));
    }

    [Fact]
    public void Self_references_and_cycles_are_reported_and_nothing_else_is()
    {
        using var context = Contexts.Sqlite();

        Plan(context).CyclicTables.Select(t => t.Name).Order().ShouldBe(["Categories", "Players", "Teams"]);
    }

    [Fact]
    public void The_order_is_deterministic()
    {
        using var context = Contexts.Sqlite();

        Names(Plan(context)).ShouldBe(Names(Plan(context)));
    }

    [Fact]
    public void Excluded_entities_and_tables_are_left_out()
    {
        using var context = Contexts.Sqlite();

        var plan = Plan(context, o => o.ExcludeEntity<Product>().ExcludeTable("Tags"));

        Names(plan).ShouldNotContain("Products");
        Names(plan).ShouldNotContain("Tags");
        Names(plan).ShouldContain("OrderLines");
        plan.HasExclusions.ShouldBeTrue();
    }

    [Fact]
    public void A_table_exclusion_with_a_schema_matches_only_that_schema()
    {
        using var context = Contexts.Sqlite();

        Names(Plan(context, o => o.ExcludeTable("Tags", "other"))).ShouldContain("Tags");
    }

    [Fact]
    public void Excluding_an_unmapped_type_is_refused()
    {
        using var context = Contexts.Sqlite();

        Should.Throw<InvalidOperationException>(() => Plan(context, o => o.ExcludeEntity<string>()))
            .Message.ShouldContain("'String' from the reset");
    }

    [Fact]
    public void A_principal_left_out_imposes_no_order()
    {
        using var context = Contexts.Sqlite();

        var plan = Plan(context, o => o.ExcludeEntity<Customer>());

        Names(plan).ShouldContain("Orders");
        Names(plan).ShouldNotContain("Customers");
    }

    [Fact]
    public void Keeping_a_table_that_references_a_reset_one_is_refused()
    {
        // Every engine suspends or defers foreign-key enforcement while it deletes, so a kept
        // dependent would be cascade-deleted (SQLite, in a transaction) or orphaned (everywhere else).
        using var context = Contexts.Sqlite();

        var error = Should.Throw<InvalidOperationException>(() => Plan(context, o => o.ExcludeEntity<OrderLine>()));

        error.Message.ShouldContain("Cannot reset 'Orders' while keeping 'OrderLines'");
        error.Message.ShouldContain("Exclude 'Orders' as well");
    }

    [Fact]
    public void Keeping_the_principal_too_makes_the_exclusion_valid()
    {
        using var context = Contexts.Sqlite();

        var plan = Plan(context, o => o.ExcludeEntity<OrderLine>().ExcludeEntity<Order>().ExcludeEntity<Product>().ExcludeEntity<Customer>());

        Names(plan).ShouldNotContain("OrderLines");
        Names(plan).ShouldNotContain("Orders");
    }

    [Fact]
    public void Catalog_references_from_outside_the_reset_are_refused_and_from_inside_allowed()
    {
        var plan = ResetPlan.Create([new ResetTable(null, "Orders"), new ResetTable(null, "Customers")]);

        // Inside the reset, or into a table the reset keeps: nothing at risk.
        plan.RefuseUnplannedReferences([new DatabaseReference("Orders", "Customers"), new DatabaseReference("Notes", "Archive")]);

        Should.Throw<InvalidOperationException>(() => plan.RefuseUnplannedReferences([new DatabaseReference("audit", "CUSTOMERS")]))
            .Message.ShouldContain("'audit', which the reset does not empty");

        // A table of the same name in another MySQL database is still outside the reset.
        Should.Throw<InvalidOperationException>(() => plan.RefuseUnplannedReferences([new DatabaseReference("Orders", "Customers", OutsideDatabase: true)]));
    }

    [Fact]
    public void On_a_case_sensitive_server_a_table_differing_only_in_case_is_outside_the_reset()
    {
        var plan = ResetPlan.Create([new ResetTable(null, "Orders"), new ResetTable(null, "Customers")]);
        DatabaseReference[] lowercase = [new DatabaseReference("orders", "Customers")];

        // Where the server folds case, orders is Orders and is being reset with its principal.
        plan.RefuseUnplannedReferences(lowercase, names: StringComparer.OrdinalIgnoreCase);

        // Where it does not, orders is a table of its own, left pointing at customers that are gone.
        Should.Throw<InvalidOperationException>(() => plan.RefuseUnplannedReferences(lowercase, names: StringComparer.Ordinal))
            .Message.ShouldContain("'orders', which the reset does not empty");
    }

    [Fact]
    public void With_schemas_a_same_named_table_in_another_schema_is_outside_the_reset()
    {
        var plan = ResetPlan.Create([new ResetTable(null, "Orders"), new ResetTable("sales", "Customers")]);

        // dbo.Orders -> sales.Customers is inside the reset.
        plan.RefuseUnplannedReferences([new DatabaseReference("Orders", "Customers", TableSchema: "dbo", ReferencedSchema: "sales")], "dbo");

        // audit.Orders is not dbo.Orders.
        Should.Throw<InvalidOperationException>(() => plan.RefuseUnplannedReferences(
            [new DatabaseReference("Orders", "Customers", TableSchema: "audit", ReferencedSchema: "sales")], "dbo"));

        // And a key into dbo.Customers — not a table this reset empties — is no concern of it.
        plan.RefuseUnplannedReferences([new DatabaseReference("X", "Customers", TableSchema: "dbo", ReferencedSchema: "dbo")], "dbo");
    }

    [Fact]
    public void The_plan_remembers_what_it_keeps()
    {
        using var context = Contexts.Sqlite();

        var plan = Plan(context, o => o.ExcludeTable("Tags"));

        plan.Retained.Select(t => t.Name).ShouldBe(["Tags"]);
        Plan(context).Retained.ShouldBeEmpty();
    }

    [Fact]
    public void A_truncated_descendant_is_refused_by_name()
    {
        ResetPlan.RefuseTruncatedDescendants([]);

        Should.Throw<InvalidOperationException>(() => ResetPlan.RefuseTruncatedDescendants([new DatabaseReference("archive", "customers")]))
            .Message.ShouldContain("Cannot reset 'customers': PostgreSQL truncates every table that inherits from it, and 'archive'");
    }

    [Fact]
    public void Schemas_are_carried_through()
    {
        using var context = new SchemaContext(
            new DbContextOptionsBuilder<SchemaContext>().UseNpgsql("Host=unused").Options);

        var plan = Plan(context);

        plan.Tables.ShouldContain(new ResetTable("sales", "Invoices"));
        plan.Tables.ShouldContain(new ResetTable("public", "Clients"));
        plan.Tables.Select(t => t.ToString()).ShouldBe(["sales.Invoices", "public.Clients"]);
    }

    [Fact]
    public void Exclusions_report_none_when_there_are_none()
    {
        using var context = Contexts.Sqlite();

        Plan(context).HasExclusions.ShouldBeFalse();
    }

    private static ResetTable T(string name) => new(null, name);

    private static (List<ResetTable> Ordered, HashSet<ResetTable> Cyclic) Order(
        Dictionary<string, string[]> references)
        => ResetPlan.Order(
            [.. references.Keys.Select(T)],
            t => references[t.Name].Select(T));

    [Fact]
    public void A_long_chain_is_ordered_without_overflowing_the_stack()
    {
        // T0 references T1, which references T2, ... so T0 must go first and T19999 last.
        const int length = 20_000;
        var references = Enumerable.Range(0, length)
            .ToDictionary(i => $"T{i:D5}", i => i + 1 < length ? new[] { $"T{i + 1:D5}" } : []);

        var (ordered, cyclic) = Order(references);

        ordered.Select(t => t.Name).ShouldBe(references.Keys);
        cyclic.ShouldBeEmpty();
    }

    [Fact]
    public void A_diamond_puts_the_shared_principal_last()
    {
        var (ordered, _) = Order(new()
        {
            ["Top"] = ["Left", "Right"],
            ["Left"] = ["Bottom"],
            ["Right"] = ["Bottom"],
            ["Bottom"] = [],
        });

        var names = ordered.Select(t => t.Name).ToList();
        names[0].ShouldBe("Top");
        names[^1].ShouldBe("Bottom");
    }

    [Fact]
    public void A_three_table_cycle_is_one_component_ordered_after_its_dependents()
    {
        var (ordered, cyclic) = Order(new()
        {
            ["A"] = ["B"],
            ["B"] = ["C"],
            ["C"] = ["A"],
            ["Dependent"] = ["A"],
            ["Principal"] = [],
            ["Lone"] = [],
        });

        cyclic.Select(t => t.Name).Order().ShouldBe(["A", "B", "C"]);
        var names = ordered.Select(t => t.Name).ToList();
        names.IndexOf("Dependent").ShouldBeLessThan(names.IndexOf("A"));
        names.Count.ShouldBe(6);
    }

    [Fact]
    public void A_cycle_referencing_a_principal_comes_before_it()
    {
        var (ordered, cyclic) = Order(new()
        {
            ["A"] = ["B", "Principal"],
            ["B"] = ["A"],
            ["Principal"] = [],
        });

        cyclic.Select(t => t.Name).Order().ShouldBe(["A", "B"]);
        ordered.Select(t => t.Name).Last().ShouldBe("Principal");
    }

    [Fact]
    public void A_self_reference_is_cyclic_on_its_own()
    {
        var (_, cyclic) = Order(new() { ["Self"] = ["Self"], ["Other"] = [] });

        cyclic.Select(t => t.Name).ShouldBe(["Self"]);
    }

    public sealed class Client
    {
        public int Id { get; set; }
    }

    public sealed class Invoice
    {
        public int Id { get; set; }

        public int ClientId { get; set; }

        public Client Client { get; set; } = null!;
    }

    public sealed class SchemaContext(DbContextOptions<SchemaContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("public");
            modelBuilder.Entity<Client>().ToTable("Clients");
            modelBuilder.Entity<Invoice>().ToTable("Invoices", "sales");
        }
    }
}
