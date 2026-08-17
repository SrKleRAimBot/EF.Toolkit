using System.Text.Json;
using EFToolkit.Audit.Api;
using EFToolkit.Audit.Capture;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Tests.Capture;

/// <summary>
///     How auditing meets the EF Core mapping features that decide what a row actually contains.
/// </summary>
/// <remarks>
///     <para>
///         An audit trail is only worth having if it agrees with the table it describes. Every
///         mapping feature below moves the boundary of what "the row" means — an owned type puts
///         another CLR object's columns on it, table-per-type spreads it over two tables, a value
///         converter changes what the column holds, a shadow property puts a column on it that no
///         CLR member exposes. Each one has a plausible-looking wrong answer that produces a trail
///         quietly missing a column.
///     </para>
///     <para>
///         Global query filters are here for the opposite reason: they must make no difference at
///         all. A filter is a read-side concern, and a change to a hidden row is still a change that
///         has to be recorded — silently skipping it would put a hole in the trail exactly where a
///         soft-deleted or cross-tenant row was touched.
///     </para>
/// </remarks>
public class EfFeatureInteropTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoMetadata
        = new Dictionary<string, object?>();

    // ---------------------------------------------------------------------------------------
    // Owned types
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void An_owned_reference_is_folded_into_its_owner()
    {
        // The owned columns are on the owner's table, so they belong in the owner's entry. Left as
        // an entity type of their own they would either produce a second entry for a row that does
        // not exist, or none at all.
        var plan = Plan<Customer>(b => b.Entity<Customer>().OwnsOne(c => c.Address));

        plan.IsAudited.ShouldBeTrue();
        plan.OwnedFolds.ShouldNotBeEmpty();
    }

    [Fact]
    public void The_owned_type_itself_produces_no_entry_of_its_own()
    {
        using var context = TestModel.Context(
            a => a.AuditAllEntities(),
            b => b.Entity<Customer>().OwnsOne(c => c.Address));

        var owned = context.Model.FindEntityType(typeof(Address))!;
        var plan = AuditEntityPlan.For(owned, context.Options());

        plan.IsAudited.ShouldBeFalse();
        plan.NotAuditedReason.ShouldNotBeNull().ShouldContain("folded into its owner");
    }

    [Fact]
    public void An_owned_reference_reaches_the_payload_under_its_navigation_path()
    {
        // The fold has to be visible in the entry, not merely planned: an auditor reading the trail
        // needs to see which of the owner's columns moved, including the owned ones.
        using var context = TestModel.Context(
            a => a.AuditAllEntities(),
            b => b.Entity<Customer>().OwnsOne(c => c.Address));

        var entityType = context.Model.FindEntityType(typeof(Customer))!;
        var options = context.Options();
        var plan = AuditEntityPlan.For(entityType, options);
        var owned = context.Model.FindEntityType(typeof(Address))!;

        var properties = new[]
        {
            entityType.FindProperty(nameof(Customer.Id))!,
            entityType.FindProperty(nameof(Customer.Name))!,
            owned.FindProperty(nameof(Address.City))!,
        };

        var payload = Write(
            plan,
            options,
            new FakeCaptureSource(entityType, AuditOperation.Insert, properties)
                .Row(1, "Ada", "Bath"));

        payload.GetProperty("new").GetProperty(nameof(Customer.Name)).GetString().ShouldBe("Ada");
        payload.GetProperty("new").GetProperty("Address.City").GetString().ShouldBe("Bath");
    }

    // ---------------------------------------------------------------------------------------
    // Complex types
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_complex_property_is_captured_under_its_path()
    {
        // A complex type's values are columns of the row that declares them, so a change to one is
        // a change to that row. They are mapped by the complex type rather than by the entity, so
        // walking the entity's own properties missed them and produced a trail that quietly
        // disagreed with the table it described.
        var plan = Plan<Invoice>(b => b.Entity<Invoice>()
            .ComplexProperty(i => i.Money, m => m.ComplexProperty(x => x.Stamp)));

        var names = plan.OwnProperties.Select(p => plan.Capture(p)!.Name).ToList();

        names.ShouldContain("Money.Amount");
        names.ShouldContain("Money.Currency");
        names.ShouldContain("Money.Stamp.By");
    }

    [Fact]
    public void A_complex_value_reaches_the_payload()
    {
        // Planned is not enough: the entry has to carry the value, which means the change tracker
        // has to be able to supply it for a property declared on a complex type.
        using var context = TestModel.Context(
            a => a.AuditAllEntities(),
            b => b.Entity<Invoice>().ComplexProperty(i => i.Money));

        var entityType = context.Model.FindEntityType(typeof(Invoice))!;
        var options = context.Options();
        var money = entityType.GetComplexProperties().Single().ComplexType;

        var properties = new[]
        {
            entityType.FindProperty(nameof(Invoice.Id))!,
            money.FindProperty(nameof(Money.Amount))!,
            money.FindProperty(nameof(Money.Currency))!,
        };

        var payload = Write(
            AuditEntityPlan.For(entityType, options),
            options,
            new FakeCaptureSource(entityType, AuditOperation.Insert, properties)
                .Row(1, 12.5m, "GBP"));

        payload.GetProperty("new").GetProperty("Money.Amount").GetDecimal().ShouldBe(12.5m);
        payload.GetProperty("new").GetProperty("Money.Currency").GetString().ShouldBe("GBP");
    }

    [Fact]
    public void A_change_inside_a_complex_value_is_reported_as_that_column_moving()
    {
        // The diff is per column, so an update that touched one member of a value object says so
        // rather than reporting the whole object as changed.
        using var context = TestModel.Context(
            a => a.AuditAllEntities(),
            b => b.Entity<Invoice>().ComplexProperty(i => i.Money));

        var entityType = context.Model.FindEntityType(typeof(Invoice))!;
        var options = context.Options();
        var money = entityType.GetComplexProperties().Single().ComplexType;

        var properties = new[]
        {
            entityType.FindProperty(nameof(Invoice.Id))!,
            money.FindProperty(nameof(Money.Amount))!,
            money.FindProperty(nameof(Money.Currency))!,
        };

        var payload = Write(
            AuditEntityPlan.For(entityType, options),
            options,
            new FakeCaptureSource(entityType, AuditOperation.Update, properties)
                .Changed([1, 12.5m, "GBP"], [1, 20m, "GBP"]));

        payload.GetProperty("changed").EnumerateArray().Select(e => e.GetString())
            .ShouldHaveSingleItem().ShouldBe("Money.Amount");

        payload.GetProperty("old").GetProperty("Money.Amount").GetDecimal().ShouldBe(12.5m);
        payload.GetProperty("new").GetProperty("Money.Amount").GetDecimal().ShouldBe(20m);
    }

    [Fact]
    public void A_complex_member_can_be_masked_on_the_value_object()
    {
        // The mask belongs where the sensitive member is declared, so a value object used by five
        // entities is not restated five times.
        var plan = Plan<Payment>(b => b.Entity<Payment>().ComplexProperty(p => p.Card));

        var captured = plan.OwnProperties
            .Select(p => plan.Capture(p)!)
            .Single(c => c.Name == "Card.Number");

        captured.IsMasked.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Inheritance
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_table_per_type_derived_entity_captures_the_columns_of_both_its_tables()
    {
        // The row spans two tables, and the trail describes the entity rather than a table — so
        // reading only the first mapping would drop every derived column.
        var plan = Plan<Employee>(b =>
        {
            b.Entity<Party>().ToTable("Parties");
            b.Entity<Employee>().ToTable("Employees");
        });

        var captured = plan.OwnProperties.Select(p => p.Name).ToList();

        captured.ShouldContain(nameof(Party.Name));
        captured.ShouldContain(nameof(Employee.Title));
    }

    [Fact]
    public void A_table_per_hierarchy_derived_entity_is_recorded_under_its_own_name()
    {
        // Both siblings share a table, so the table name cannot identify what changed. The entity
        // type name is what makes a TPH trail readable at all.
        var plan = Plan<CreditCard>(b => b.Entity<Card>()
            .HasDiscriminator<string>("Kind")
            .HasValue<Card>("card")
            .HasValue<CreditCard>("credit"));

        plan.EntityTypeName.ShouldBe(nameof(CreditCard));
        plan.OwnProperties.Select(p => p.Name).ShouldContain(nameof(CreditCard.Limit));
    }

    [Fact]
    public void A_shadow_discriminator_is_captured_like_any_other_column()
    {
        // Auditing reads through the change tracker, which can read a shadow property — so unlike
        // the explicit bulk API, there is nothing here that needs refusing.
        var plan = Plan<Card>(b => b.Entity<Card>()
            .HasDiscriminator<string>("Kind")
            .HasValue<Card>("card")
            .HasValue<CreditCard>("credit"));

        plan.OwnProperties.Select(p => p.Name).ShouldContain("Kind");
    }

    // ---------------------------------------------------------------------------------------
    // Shadow properties and value converters
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_shadow_property_is_captured()
    {
        var plan = Plan<Ticket>(b => b.Entity<Ticket>().Property<string>("LastTouchedBy"));

        plan.OwnProperties.Select(p => p.Name).ShouldContain("LastTouchedBy");
    }

    [Fact]
    public void A_converted_value_is_recorded_as_the_store_holds_it()
    {
        // The trail has to agree with the column it describes. An enum mapped to text that was
        // recorded as its ordinal would disagree with every query run against the table.
        using var context = TestModel.Context(
            a => a.AuditAllEntities(),
            b => b.Entity<Ticket>().Property(t => t.State).HasConversion<string>());

        var entityType = context.Model.FindEntityType(typeof(Ticket))!;
        var options = context.Options();

        var properties = new[]
        {
            entityType.FindProperty(nameof(Ticket.Id))!,
            entityType.FindProperty(nameof(Ticket.State))!,
        };

        var payload = Write(
            AuditEntityPlan.For(entityType, options),
            options,
            new FakeCaptureSource(entityType, AuditOperation.Insert, properties)
                .Row(1, TicketState.Closed));

        payload.GetProperty("new").GetProperty(nameof(Ticket.State)).GetString()
            .ShouldBe(nameof(TicketState.Closed));
    }

    [Fact]
    public void A_converted_value_that_did_not_move_is_not_reported_as_a_change()
    {
        // Compared through the property's own ValueComparer. EF marks a property modified when it
        // is assigned at all, so trusting IsModified would fill the trail with updates that changed
        // nothing — and for a converted or collection-valued property, plain Equals is the wrong
        // question anyway.
        using var context = TestModel.Context(
            a => a.AuditAllEntities(),
            b => b.Entity<Ticket>().Property(t => t.State).HasConversion<string>());

        var entityType = context.Model.FindEntityType(typeof(Ticket))!;
        var state = entityType.FindProperty(nameof(Ticket.State))!;

        AuditValues.AreEqual(state, TicketState.Closed, TicketState.Closed).ShouldBeTrue();
        AuditValues.AreEqual(state, TicketState.Open, TicketState.Closed).ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------------------
    // Global query filters
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void An_entity_behind_a_global_query_filter_is_audited_normally()
    {
        // Deliberately no interaction. A filter decides what a query returns; it says nothing about
        // whether a change is worth recording, and a trail that skipped filtered rows would be
        // missing entries exactly where a soft-deleted or cross-tenant row was touched.
        var plan = Plan<Ticket>(b => b.Entity<Ticket>().HasQueryFilter(t => !t.IsArchived));

        plan.IsAudited.ShouldBeTrue();
        plan.OwnProperties.Select(p => p.Name).ShouldContain(nameof(Ticket.IsArchived));
    }

    [Fact]
    public void A_filtered_entity_produces_the_same_entry_as_an_unfiltered_one()
    {
        var filtered = Plan<Ticket>(b => b.Entity<Ticket>().HasQueryFilter(t => !t.IsArchived));
        var plain = Plan<Ticket>(b => b.Entity<Ticket>());

        filtered.OwnProperties.Select(p => p.Name)
            .ShouldBe(plain.OwnProperties.Select(p => p.Name));

        filtered.KeyProperties.Select(p => p.Name)
            .ShouldBe(plain.KeyProperties.Select(p => p.Name));
    }

    private static AuditEntityPlan Plan<TEntity>(Action<ModelBuilder> model)
        where TEntity : class
    {
        using var context = TestModel.Context(a => a.AuditAllEntities(), model);

        return AuditEntityPlan.For(context.Model.FindEntityType(typeof(TEntity))!, context.Options());
    }

    private static JsonElement Write(
        AuditEntityPlan plan,
        AuditOptions options,
        FakeCaptureSource source)
    {
        using var writer = new AuditPayloadWriter(options);

        return JsonDocument.Parse(
            writer.Write(
                source.Operation,
                AuditSourceProjection.Create(source, plan),
                source,
                0,
                NoMetadata,
                reason: null)!).RootElement;
    }

    public class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public Address Address { get; set; } = new();
    }

    public class Address
    {
        public string Line1 { get; set; } = "";
        public string City { get; set; } = "";
    }

    public class Party
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class Employee : Party
    {
        public string? Title { get; set; }
    }

    public class Card
    {
        public int Id { get; set; }
    }

    public sealed class CreditCard : Card
    {
        public decimal Limit { get; set; }
    }

    public class Ticket
    {
        public int Id { get; set; }
        public TicketState State { get; set; }
        public bool IsArchived { get; set; }
    }

    public class Invoice
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public Money Money { get; set; } = new();
    }

    public class Money
    {
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "";
        public Stamp Stamp { get; set; } = new();
    }

    public class Stamp
    {
        public string By { get; set; } = "";
    }

    public class Payment
    {
        public int Id { get; set; }
        public PaymentCard Card { get; set; } = new();
    }

    /// <summary>Carries the mask on the value object, where the sensitive member is declared.</summary>
    public class PaymentCard
    {
        [AuditMask]
        public string Number { get; set; } = "";

        public string Holder { get; set; } = "";
    }

    public enum TicketState
    {
        Open,
        Closed,
    }
}
