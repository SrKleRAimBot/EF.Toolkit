using EFToolkit.Audit.Api;
using EFToolkit.Audit.Capture;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EFToolkit.Audit.Tests.Capture;

/// <summary>
///     What the factory stamps on an entry: the actor, the tenant, the clock and the key.
/// </summary>
public class AuditEntryFactoryTests
{
    [Fact]
    public async Task Takes_the_timestamp_from_the_configured_clock()
    {
        var entry = await OneAsync();

        // Read from a TimeProvider rather than from static state, which is what makes a timestamp
        // assertable at all.
        entry.OccurredAt.ShouldBe(FixedTimeProvider.Instant);
    }

    [Fact]
    public async Task Generates_time_ordered_keys_by_default()
    {
        var first = await ManyAsync(25);
        await Task.Delay(5, TestContext.Current.CancellationToken);
        var second = await ManyAsync(25);

        var keys = first.Concat(second).Select(e => (Guid)e.Key!).ToList();
        keys.Distinct().Count().ShouldBe(50);

        // UUIDv7 puts a 48-bit millisecond timestamp in the leading bytes and fills the rest at
        // random, so keys made in the same millisecond have no order between them — the property
        // worth asserting is that later keys sort after earlier ones, at that granularity.
        //
        // Compared big-endian, because that is how a database sorts a uuid. .NET's own Guid
        // comparison walks the fields in a different order and would answer a different question.
        Timestamp(keys[0]).ShouldBeLessThan(Timestamp(keys[^1]));

        static long Timestamp(Guid id)
        {
            Span<byte> bytes = stackalloc byte[16];
            id.TryWriteBytes(bytes, bigEndian: true, out _);

            return ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
                | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
        }
    }

    [Fact]
    public async Task Uses_a_registered_provider_and_prefers_its_batch_overload()
    {
        var provider = new SequentialIdProvider();

        var entries = await ManyAsync(
            5,
            a => a.IdsFrom<SequentialIdProvider, string>(),
            services => services.AddSingleton(provider),
            typeof(string));

        entries.Select(e => e.Key).ShouldBe(["aud_0001", "aud_0002", "aud_0003", "aud_0004", "aud_0005"]);

        // One call for the whole run, not one per entry: a bulk-audited operation asks for as many
        // keys as it wrote rows.
        provider.BatchCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Reaches_application_services_for_a_delegate_key_source()
    {
        var entries = await ManyAsync(
            2,
            a => a.Ids<string>(sp => sp.GetRequiredService<SequentialIdProvider>().Generate()),
            services => services.AddSingleton<SequentialIdProvider>(),
            typeof(string));

        entries.Select(e => e.Key).ShouldBe(["aud_0001", "aud_0002"]);
    }

    [Fact]
    public async Task Prefers_an_ambient_scope_over_the_configured_actor()
    {
        using var scope = AuditScope.Begin(new AuditActor("scope-actor"));

        var entry = await OneAsync(a => a.Actor(() => new AuditActor("configured")));

        // A scope is how a background job says who it is acting as, and how one operation overrides
        // a request-scoped default without reconfiguring anything.
        entry.ActorId.ShouldBe("scope-actor");
    }

    [Fact]
    public async Task Falls_back_to_the_configured_actor_with_no_scope()
    {
        var entry = await OneAsync(a => a.Actor(() => new AuditActor("configured", "Config")));

        entry.ActorId.ShouldBe("configured");
        entry.ActorName.ShouldBe("Config");
    }

    [Fact]
    public async Task Refuses_an_entry_with_no_actor_when_one_is_required()
    {
        var exception = await Should.ThrowAsync<AuditNotSupportedException>(
            () => OneAsync(a => a.RequireActor()).AsTask());

        exception.Message.ShouldContain("RequireActor()");
    }

    [Fact]
    public async Task Reads_the_tenant_out_of_the_captured_columns()
    {
        var entry = await OneAsync(a => a.MultiTenant(t => t.FromEntityProperty()));

        // The bulk path hands over values, not meaning, so the tenant is found among the columns
        // rather than being supplied by the source.
        entry.TenantId.ShouldBe("acme");
    }

    [Fact]
    public async Task Refuses_an_entry_with_no_tenant_when_one_is_required()
    {
        var exception = await Should.ThrowAsync<AuditNotSupportedException>(
            () => OneAsync(
                a => a.MultiTenant(t => t.FromEntityProperty().Require()),
                tenant: "").AsTask());

        // An empty tenant column is no tenant, not a tenant named "". A row recorded that way is
        // invisible to every tenant-scoped query that will later look for it.
        exception.Message.ShouldContain("has no tenant");
    }

    private static async ValueTask<AuditEntry> OneAsync(
        Action<AuditOptionsBuilder>? configure = null,
        string tenant = "acme")
        => (await CreateAsync(1, configure, tenant: tenant)).ShouldHaveSingleItem();

    private static async ValueTask<IReadOnlyList<AuditEntry>> ManyAsync(
        int rows,
        Action<AuditOptionsBuilder>? configure = null,
        Action<IServiceCollection>? services = null,
        Type? keyType = null)
        => await CreateAsync(rows, configure, services, keyType);

    private static async ValueTask<IReadOnlyList<AuditEntry>> CreateAsync(
        int rows,
        Action<AuditOptionsBuilder>? configure = null,
        Action<IServiceCollection>? services = null,
        Type? keyType = null,
        string tenant = "acme")
    {
        var applicationServices = new ServiceCollection();
        services?.Invoke(applicationServices);

        using var context = TestModel.Context(
            configure,
            b => b.Entity<Order>().IsAudited(),
            applicationServices.BuildServiceProvider());

        var entityType = context.Model.FindEntityType(typeof(Order))!;
        var options = context.Options();

        var factory = (IAuditEntryFactory)Activator.CreateInstance(
            typeof(AuditEntryFactory<>).MakeGenericType(keyType ?? typeof(Guid)),
            options,
            context.GetService<IDbContextOptions>())!;

        var source = new FakeCaptureSource(
            entityType,
            AuditOperation.Insert,
            entityType.FindProperty(nameof(Order.Id))!,
            entityType.FindProperty(nameof(Order.Reference))!,
            entityType.FindProperty(nameof(Order.Status))!,
            entityType.FindProperty(nameof(Order.TenantId))!);

        for (var i = 1; i <= rows; i++)
        {
            source.Row(i, $"REF-{i}", "Draft", tenant);
        }

        return await factory.CreateAsync([source], TestContext.Current.CancellationToken);
    }
}
