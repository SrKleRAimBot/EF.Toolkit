using EFToolkit.Audit.Api;
using EFToolkit.Audit.Capture;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Tests.Capture;

/// <summary>
///     Which types and which of their properties end up in the trail.
/// </summary>
public class AuditEntityPlanTests
{
    [Fact]
    public void Audits_nothing_until_a_type_says_so()
    {
        var plan = Plan<Anonymous>(b => b.Entity<Anonymous>());

        plan.IsAudited.ShouldBeFalse();
        plan.NotAuditedReason.ShouldNotBeNull().ShouldContain("not registered");
    }

    [Fact]
    public void Audits_a_type_registered_fluently()
        => Plan<Order>(b => b.Entity<Order>().IsAudited()).IsAudited.ShouldBeTrue();

    [Fact]
    public void Audits_a_type_registered_by_attribute()
        => Plan<Credential>(b => b.Entity<Credential>()).IsAudited.ShouldBeTrue();

    [Fact]
    public void Audits_every_type_once_the_default_is_inverted()
    {
        var plan = Plan<Anonymous>(b => b.Entity<Anonymous>(), a => a.AuditAllEntities());

        plan.IsAudited.ShouldBeTrue();
    }

    [Fact]
    public void Leaves_out_a_type_that_opted_out_of_the_inverted_default()
    {
        var fluent = Plan<Order>(b => b.Entity<Order>().IsNotAudited(), a => a.AuditAllEntities());
        fluent.IsAudited.ShouldBeFalse();

        var attribute = Plan<Telemetry>(b => b.Entity<Telemetry>(), a => a.AuditAllEntities());
        attribute.IsAudited.ShouldBeFalse();
    }

    [Fact]
    public void Never_audits_the_audit_entry_itself()
    {
        using var context = TestModel.Context(onModelCreating: b => b.Entity<Order>().IsAudited(),
            configure: a => a.AuditAllEntities());

        var entryType = context.Model.GetEntityTypes()
            .Single(e => typeof(AuditEntry).IsAssignableFrom(e.ClrType));

        var plan = AuditEntityPlan.For(entryType, context.Options());

        // Otherwise every entry would produce an entry, and so on.
        plan.IsAudited.ShouldBeFalse();
        plan.NotAuditedReason.ShouldNotBeNull().ShouldContain("audit entry type itself");
    }

    [Fact]
    public void Captures_every_property_of_an_audited_type_by_default()
    {
        var plan = Plan<Order>(b => b.Entity<Order>().IsAudited());

        // The important half of the property rule: a column added later is audited from the moment
        // it exists, without anyone remembering to say so.
        plan.OwnProperties.Select(p => p.Name).ShouldContain(nameof(Order.Total));
        plan.OwnProperties.Select(p => p.Name).ShouldContain(nameof(Order.CardNumber));
        plan.OwnProperties.Select(p => p.Name).ShouldContain(nameof(Order.InternalNotes));
    }

    [Fact]
    public void Drops_a_property_excluded_fluently()
    {
        var plan = Plan<Order>(
            b => b.Entity<Order>().IsAudited(a => a.Exclude(o => o.InternalNotes)));

        plan.OwnProperties.Select(p => p.Name).ShouldNotContain(nameof(Order.InternalNotes));
    }

    [Fact]
    public void Drops_a_property_excluded_by_attribute()
    {
        var plan = Plan<Credential>(b => b.Entity<Credential>());

        plan.OwnProperties.Select(p => p.Name).ShouldNotContain(nameof(Credential.Scratch));
    }

    [Fact]
    public void Masks_a_property_marked_either_way()
    {
        var fluent = Plan<Order>(b => b.Entity<Order>().IsAudited(a => a.Mask(o => o.CardNumber)));
        Capture(fluent, nameof(Order.CardNumber)).IsMasked.ShouldBeTrue();

        var attribute = Plan<Credential>(b => b.Entity<Credential>());
        Capture(attribute, nameof(Credential.Secret)).IsMasked.ShouldBeTrue();
    }

    [Fact]
    public void Masks_every_property_a_model_wide_rule_matches()
    {
        var plan = Plan<Order>(
            b => b.Entity<Order>().IsAudited(),
            a => a.MaskProperties(p => p.Name.EndsWith("Number", StringComparison.Ordinal)));

        Capture(plan, nameof(Order.CardNumber)).IsMasked.ShouldBeTrue();
        Capture(plan, nameof(Order.Reference)).IsMasked.ShouldBeFalse();
    }

    [Fact]
    public void Narrows_the_operations_a_type_is_audited_for()
    {
        var plan = Plan<Order>(
            b => b.Entity<Order>().IsAudited(a => a.Operations(AuditOperations.Insert)));

        plan.Audits(AuditOperation.Insert).ShouldBeTrue();
        plan.Audits(AuditOperation.Update).ShouldBeFalse();
        plan.Audits(AuditOperation.Delete).ShouldBeFalse();
    }

    [Fact]
    public void Builds_the_entry_key_from_the_primary_key_by_default()
    {
        var plan = Plan<Membership>(b => b.Entity<Membership>(m =>
        {
            m.HasKey(x => new { x.GroupId, x.UserId });
            m.IsAudited();
        }));

        plan.KeyProperties.Select(p => p.Name).ShouldBe([nameof(Membership.GroupId), nameof(Membership.UserId)]);
    }

    [Fact]
    public void Builds_the_entry_key_from_a_projection_when_one_is_given()
    {
        var plan = Plan<Order>(
            b => b.Entity<Order>().IsAudited(a => a.KeyFrom(o => o.Reference)));

        plan.KeyProperties.ShouldHaveSingleItem().Name.ShouldBe(nameof(Order.Reference));

        // The real key is still written into the payload, so nothing is lost.
        plan.PrimaryKey.ShouldHaveSingleItem().Name.ShouldBe(nameof(Order.Id));
    }

    [Fact]
    public void Finds_the_tenant_property_the_settings_name()
    {
        var plan = Plan<Order>(
            b => b.Entity<Order>().IsAudited(),
            a => a.MultiTenant(t => t.FromEntityProperty()));

        plan.TenantProperty!.Name.ShouldBe(nameof(Order.TenantId));
    }

    private static AuditPropertyPlan Capture(AuditEntityPlan plan, string property)
        => plan.OwnProperties
            .Where(p => p.Name == property)
            .Select(plan.Capture)
            .Single()
            .ShouldNotBeNull();

    private static AuditEntityPlan Plan<TEntity>(
        Action<ModelBuilder> model,
        Action<AuditOptionsBuilder>? configure = null)
        where TEntity : class
    {
        using var context = TestModel.Context(configure, model);

        return AuditEntityPlan.For(context.Model.FindEntityType(typeof(TEntity))!, context.Options());
    }
}
