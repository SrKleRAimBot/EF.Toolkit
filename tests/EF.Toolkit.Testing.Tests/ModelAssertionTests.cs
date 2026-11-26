using EFToolkit.Testing.Tests.Infrastructure;
using EFToolkit.Testing.Tests.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFToolkit.Testing.Tests;

public sealed class ModelAssertionTests
{
    private static TContext Sqlite<TContext>(Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
        => create(new DbContextOptionsBuilder<TContext>().UseSqlite("Data Source=:memory:").Options);

    [Fact]
    public void A_model_matching_its_snapshot_has_no_pending_changes()
    {
        using var context = Sqlite<MigratedContext>(o => new(o));

        Should.NotThrow(context.ShouldHaveNoPendingModelChanges);
    }

    [Fact]
    public void A_drifted_model_lists_what_the_next_migration_would_contain()
    {
        using var context = Sqlite<DriftedContext>(o => new(o));

        var message = Should.Throw<EFToolkitAssertionException>(context.ShouldHaveNoPendingModelChanges).Message;

        message.ShouldStartWith("'DriftedContext' has model changes that are not in a migration.");
        message.ShouldContain("  - AddColumn Gadgets.Colour");
        message.ShouldContain("  - CreateIndex Gadgets.IX_Gadgets_Name");
        message.ShouldEndWith("Add a migration with: dotnet ef migrations add <Name>");
    }

    [Fact]
    public void A_context_without_migrations_says_so()
    {
        using var context = Sqlite<UnmigratedContext>(o => new(o));

        Should.Throw<EFToolkitAssertionException>(context.ShouldHaveNoPendingModelChanges)
            .Message.ShouldContain("'UnmigratedContext' has no migrations");
    }

    [Fact]
    public void Operations_are_described_by_their_target()
    {
        DbContextModelAssertions.Describe(new AddColumnOperation { Schema = "s", Table = "T", Name = "C" })
            .ShouldBe("AddColumn s.T.C");
        DbContextModelAssertions.Describe(new CreateTableOperation { Name = "T" }).ShouldBe("CreateTable T");
        DbContextModelAssertions.Describe(new EnsureSchemaOperation { Name = "s" }).ShouldBe("EnsureSchema s");
        DbContextModelAssertions.Describe(new SqlOperation { Sql = "x" }).ShouldBe("Sql");
        DbContextModelAssertions.Describe(new DropPrimaryKeyOperation { Table = "T", Name = "PK" }).ShouldBe("DropPrimaryKey T.PK");
        DbContextModelAssertions.Describe(new AlterDatabaseOperation()).ShouldBe("AlterDatabase");
        DbContextModelAssertions.Describe(new Custom()).ShouldBe("Custom");
        DbContextModelAssertions.Describe(new TableOnly { Table = "T" }).ShouldBe("TableOnly T");
    }

    [Fact]
    public void A_table_mapping_passes_when_it_matches()
    {
        using var context = Contexts.Npgsql();

        context.ShouldMapTable<Customer>("Customers");
        context.ShouldMapTable<Customer>("Customers", schema: null);
    }

    [Fact]
    public void A_table_mapping_reports_what_it_maps_to()
    {
        using var context = Contexts.Npgsql();

        Should.Throw<EFToolkitAssertionException>(() => context.ShouldMapTable<Customer>("customers"))
            .Message.ShouldBe("Expected 'Customer' to map to table 'customers', but it maps to 'Customers'.");
        Should.Throw<EFToolkitAssertionException>(() => context.ShouldMapTable<Customer>("Customers", "sales"))
            .Message.ShouldBe("Expected 'Customer' to map to table 'sales.Customers', but it maps to 'Customers'.");
    }

    [Fact]
    public void A_schema_is_checked_when_given()
    {
        using var context = new ResetPlanTests.SchemaContext(
            new DbContextOptionsBuilder<ResetPlanTests.SchemaContext>().UseNpgsql("Host=unused").Options);

        context.ShouldMapTable<ResetPlanTests.Invoice>("Invoices", "sales");
        Should.Throw<EFToolkitAssertionException>(() => context.ShouldMapTable<ResetPlanTests.Invoice>("Invoices", "public"))
            .Message.ShouldContain("but it maps to 'sales.Invoices'");
    }

    [Fact]
    public void A_view_or_unmapped_type_is_reported_as_such()
    {
        using var context = Contexts.Npgsql();

        Should.Throw<EFToolkitAssertionException>(() => context.ShouldMapTable<CustomerSummary>("CustomerSummaries"))
            .Message.ShouldBe(
                "Expected 'CustomerSummary' to map to table 'CustomerSummaries', but it maps to the view 'CustomerSummaries'.");
        Should.Throw<EFToolkitAssertionException>(() => context.ShouldMapTable<RecentOrder>("RecentOrders"))
            .Message.ShouldBe("Expected 'RecentOrder' to map to table 'RecentOrders', but it maps to no table.");
        Should.Throw<EFToolkitAssertionException>(() => context.ShouldMapTable<Uri>("Uris"))
            .Message.ShouldBe("Expected 'Uri' to be an entity type in 'ShopContext', but it is not mapped.");
    }

    [Fact]
    public void An_index_passes_on_its_exact_properties_in_order()
    {
        using var context = Contexts.Npgsql();

        context.ShouldHaveIndex<Customer>(c => c.Email);
        context.ShouldHaveIndex<Customer>(c => c.Email, unique: true);
        context.ShouldHaveIndex<Customer>(c => new { c.Name, c.Tier });
        context.ShouldHaveIndex<Customer>(c => new { c.Name, c.Tier }, unique: false);
        context.ShouldHaveIndex<Order>(o => o.CustomerId);
    }

    [Fact]
    public void A_missing_index_lists_the_ones_there_are()
    {
        using var context = Contexts.Npgsql();

        var message = Should.Throw<EFToolkitAssertionException>(() => context.ShouldHaveIndex<Customer>(c => new { c.Tier, c.Name }))
            .Message;

        message.ShouldStartWith("Expected 'Customer' to have an index on (Tier, Name), but its indexes are:");
        message.ShouldContain("  - unique IX_Customers_Email (Email)");
        message.ShouldContain("  - IX_Customers_Name_Tier (Name, Tier)");
    }

    [Fact]
    public void An_entity_with_no_indexes_says_so()
    {
        using var context = Contexts.Npgsql();

        Should.Throw<EFToolkitAssertionException>(() => context.ShouldHaveIndex<Tag>(t => t.Label))
            .Message.ShouldBe("Expected 'Tag' to have an index on (Label), but it has no indexes.");
    }

    [Fact]
    public void Uniqueness_is_checked_when_given()
    {
        using var context = Contexts.Npgsql();

        Should.Throw<EFToolkitAssertionException>(() => context.ShouldHaveIndex<Customer>(c => c.Email, unique: false))
            .Message.ShouldBe("Expected the index on 'Customer' (Email) to be non-unique, but it is unique.");
        Should.Throw<EFToolkitAssertionException>(() => context.ShouldHaveIndex<Customer>(c => new { c.Name, c.Tier }, unique: true))
            .Message.ShouldBe("Expected the index on 'Customer' (Name, Tier) to be unique, but it is non-unique.");
    }

    [Fact]
    public void A_selector_that_is_not_a_property_is_refused()
    {
        using var context = Contexts.Npgsql();

        Should.Throw<ArgumentException>(() => context.ShouldHaveIndex<Customer>(c => c.Name.Length))
            .ParamName.ShouldBe("properties");
        Should.Throw<ArgumentException>(() => context.ShouldHaveIndex<Customer>(c => new { Upper = c.Name.ToUpperInvariant() }));
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        using var context = Contexts.Npgsql();

        Should.Throw<ArgumentNullException>(() => DbContextModelAssertions.ShouldHaveNoPendingModelChanges(null!));
        Should.Throw<ArgumentNullException>(() => DbContextModelAssertions.ShouldMapTable<Customer>(null!, "x"));
        Should.Throw<ArgumentException>(() => context.ShouldMapTable<Customer>(" "));
        Should.Throw<ArgumentNullException>(() => DbContextModelAssertions.ShouldHaveIndex<Customer>(null!, c => c.Id));
        Should.Throw<ArgumentNullException>(() => context.ShouldHaveIndex<Customer>(null!));
    }

    [Fact]
    public void The_exception_has_the_standard_constructors()
    {
        new EFToolkitAssertionException().Message.ShouldNotBeNull();
        new EFToolkitAssertionException("m").Message.ShouldBe("m");
        var inner = new InvalidOperationException();
        new EFToolkitAssertionException("m", inner).InnerException.ShouldBeSameAs(inner);
    }

    private sealed class Custom : MigrationOperation;

    private sealed class TableOnly : MigrationOperation
    {
        public string? Table { get; set; }
    }
}
