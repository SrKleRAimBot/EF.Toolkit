using EFToolkit.Audit.Api;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Tests.Infrastructure;

/// <summary>
///     What the model refuses to build.
/// </summary>
/// <remarks>
///     Every refusal here has the same shape: two pieces of configuration that cannot both be
///     honoured, resolved by failing at startup rather than by a precedence rule nobody remembers.
///     The alternative is a property quietly missing from the trail and a documentation page
///     explaining why.
/// </remarks>
public class AuditModelConventionTests
{
    [Fact]
    public void Refuses_a_type_registered_and_unregistered_at_once()
    {
        var exception = Should.Throw<AuditNotSupportedException>(
            () => Build(b => b.Entity<Telemetry>().IsAudited()));

        exception.Message.ShouldContain("disagree");
        exception.Message.ShouldContain(nameof(NotAuditedAttribute));
    }

    [Fact]
    public void Refuses_a_type_unregistered_fluently_but_registered_by_attribute()
    {
        var exception = Should.Throw<AuditNotSupportedException>(
            () => Build(b => b.Entity<Credential>().IsNotAudited()));

        exception.Message.ShouldContain("disagree");
    }

    [Fact]
    public void Refuses_a_property_both_masked_and_ignored()
    {
        var exception = Should.Throw<AuditNotSupportedException>(
            () => Build(b => b.Entity<Credential>().IsAudited(a => a.Mask(c => c.Scratch))));

        // Masking records that it changed; ignoring records nothing. There is no reading of the
        // two together that is not a guess.
        exception.Message.ShouldContain("cannot both hold");
    }

    [Fact]
    public void Refuses_a_property_both_excluded_and_masked()
    {
        var exception = Should.Throw<AuditNotSupportedException>(
            () => Build(b => b.Entity<Credential>().IsAudited(a => a.Exclude(c => c.Secret))));

        exception.Message.ShouldContain(nameof(AuditMaskAttribute));
    }

    [Fact]
    public void Refuses_a_name_that_is_not_a_mapped_property()
    {
        var exception = Should.Throw<AuditNotSupportedException>(
            () => Build(b => b.Entity<Order>(e =>
            {
                e.Ignore(o => o.InternalNotes);
                e.IsAudited(a => a.Exclude(o => o.InternalNotes));
            })));

        // A typo here silently stops excluding exactly what it was meant to.
        exception.Message.ShouldContain("not a mapped property");
    }

    [Fact]
    public void Refuses_auditing_a_type_with_no_key()
    {
        var exception = Should.Throw<AuditNotSupportedException>(
            () => Build(b => b.Entity<Order>(e =>
            {
                e.HasNoKey();
                e.IsAudited();
            })));

        exception.Message.ShouldContain("no primary key");
    }

    [Fact]
    public void Accepts_a_type_registered_only_by_attribute()
        => Should.NotThrow(() => Build(b => b.Entity<Credential>()));

    [Fact]
    public void Accepts_fluent_configuration_that_only_adds_to_the_attribute()
        => Should.NotThrow(
            () => Build(b => b.Entity<Credential>().IsAudited(a => a.Exclude(c => c.Name))));

    private static void Build(Action<ModelBuilder> model)
    {
        using var context = TestModel.Context(onModelCreating: model);
        _ = context.Model;
    }
}
