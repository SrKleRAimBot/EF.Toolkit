using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Tests.Configuration;

public class AuditOptionsBuilderTests
{
    [Fact]
    public void Defaults_to_an_audit_schema_and_opt_in_registration()
    {
        var options = AuditOptions.Default;

        options.Schema.ShouldBe("audit");
        options.AuditAllEntities.ShouldBeFalse();
        options.Operations.ShouldBe(AuditOperations.All);
        options.Atomicity.ShouldBe(AuditAtomicity.SameTransaction);
        options.OnAuditFailure.ShouldBe(AuditFailure.Throw);
        options.KeyType.ShouldBe(typeof(Guid));
    }

    [Fact]
    public void Refuses_auditing_nothing_at_all()
    {
        var builder = new AuditOptionsBuilder(AuditOptions.Default);

        var exception = Should.Throw<AuditNotSupportedException>(
            () => builder.Operations(AuditOperations.None));

        exception.Message.ShouldContain("audits nothing at all");
    }

    [Fact]
    public void Refuses_a_negative_value_length()
        => Should.Throw<ArgumentOutOfRangeException>(
            () => new AuditOptionsBuilder(AuditOptions.Default).MaxValueLength(-1));

    [Fact]
    public void Refuses_a_blank_table_name()
        => Should.Throw<ArgumentException>(
            () => new AuditOptionsBuilder(AuditOptions.Default).TableName("  "));

    [Fact]
    public void Ids_sets_the_key_type_and_clears_the_other_sources()
    {
        var builder = new AuditOptionsBuilder(AuditOptions.Default).BigIntKeys();
        builder.Options.StoreGeneratedIds.ShouldBeTrue();

        builder.Ids<string>(static _ => "x");

        builder.Options.KeyType.ShouldBe(typeof(string));
        builder.Options.StoreGeneratedIds.ShouldBeFalse();
        builder.Options.IdProviderType.ShouldBeNull();
        builder.Options.IdFactory.ShouldNotBeNull();
    }

    [Fact]
    public void IdsFrom_records_the_provider_and_the_key_type()
    {
        var builder = new AuditOptionsBuilder(AuditOptions.Default)
            .IdsFrom<SequentialIdProvider, string>();

        builder.Options.KeyType.ShouldBe(typeof(string));
        builder.Options.IdProviderType.ShouldBe(typeof(SequentialIdProvider));
        builder.Options.IdFactory.ShouldBeNull();
    }

    [Fact]
    public void MultiTenant_defaults_to_the_conventional_property()
    {
        var builder = new AuditOptionsBuilder(AuditOptions.Default)
            .MultiTenant(t => t.FromEntityProperty());

        // The property Finbuckle.MultiTenant adds, which is why the default is worth having.
        builder.Options.TenantPropertyName.ShouldBe("TenantId");
        builder.Options.IsMultiTenant.ShouldBeTrue();
    }

    // The three refusals below fire as the context is constructed, which is where EF validates
    // options. That is earlier than the first query and much earlier than the first save, so a
    // configuration that cannot be honoured never reaches a request.

    [Fact]
    public void Refuses_a_sink_outside_the_transaction_it_is_told_to_share()
    {
        var exception = Should.Throw<AuditNotSupportedException>(
            () => TestModel.Context(a => a.WriteToContext<AuditTestContext>()));

        exception.Message.ShouldContain("cannot be honoured");
        exception.Message.ShouldContain("BestEffort");
    }

    [Fact]
    public void Accepts_an_external_context_once_the_guarantee_is_given_up()
    {
        using var context = TestModel.Context(a => a
            .WriteToContext<AuditTestContext>()
            .Atomicity(AuditAtomicity.BestEffort));

        Should.NotThrow(() => context.Model);
    }

    [Fact]
    public void Refuses_requiring_a_tenant_nothing_could_supply()
    {
        var exception = Should.Throw<AuditNotSupportedException>(
            () => TestModel.Context(a => a.MultiTenant(t => t.Require())));

        exception.Message.ShouldContain("no tenant source was configured");
    }

    [Fact]
    public void Refuses_store_generated_guid_keys()
    {
        var exception = Should.Throw<AuditNotSupportedException>(
            () => TestModel.Context(a => a.StoreGeneratedIds<Guid>()));

        exception.Message.ShouldContain("neither ordered nor free to read back");
    }
}
