using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Tests.Infrastructure;

/// <summary>
///     The audit table, as the model customizer adds it.
/// </summary>
public class AuditModelBuilderTests
{
    [Fact]
    public void Adds_the_audit_table_without_the_application_mentioning_it()
    {
        var entityType = EntryType();

        entityType.ClrType.ShouldBe(typeof(AuditEntry<Guid>));
        entityType.GetTableName().ShouldBe("AuditEntries");
        entityType.GetSchema().ShouldBe("audit");
    }

    [Fact]
    public void Closes_the_entry_type_over_the_configured_key()
    {
        EntryType(a => a.Ids<string>(static _ => "x")).ClrType.ShouldBe(typeof(AuditEntry<string>));
        EntryType(a => a.BigIntKeys()).ClrType.ShouldBe(typeof(AuditEntry<long>));
    }

    [Fact]
    public void Leaves_a_client_generated_key_to_the_application()
    {
        var id = EntryType().FindProperty("Id")!;

        // Saying so is what keeps a bulk insert of entries from having to read anything back.
        id.ValueGenerated.ShouldBe(ValueGenerated.Never);
    }

    [Fact]
    public void Lets_the_database_generate_a_bigint_key()
    {
        var id = EntryType(a => a.BigIntKeys()).FindProperty("Id")!;

        id.ValueGenerated.ShouldBe(ValueGenerated.OnAdd);
    }

    [Fact]
    public void Stores_the_payload_as_jsonb_on_postgres()
        => EntryType().FindProperty(nameof(AuditEntry.Changes))!
            .GetColumnType().ShouldBe("jsonb");

    [Fact]
    public void Indexes_the_history_of_one_row()
    {
        var index = Indexes()
            .Single(i => i.Properties.Select(p => p.Name)
                .SequenceEqual([
                    nameof(AuditEntry.EntityType),
                    nameof(AuditEntry.EntityKey),
                    nameof(AuditEntry.OccurredAt)
                ]));

        // Newest first, because that is the question — what happened to this row lately.
        index.IsDescending.ShouldBe([false, false, true]);
    }

    [Fact]
    public void Indexes_the_payload_with_gin()
    {
        var index = Indexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(AuditEntry.Changes)]));

        // A B-tree index over a jsonb column answers equality on the whole document and nothing
        // else, which is not what anyone asks of an audit payload.
        index.FindAnnotation("Npgsql:IndexMethod")!.Value.ShouldBe("gin");
        index.FindAnnotation("Npgsql:IndexOperators")!.Value.ShouldBe(new[] { "jsonb_path_ops" });
    }

    [Fact]
    public void Creates_the_tenant_index_only_for_a_multi_tenant_model()
    {
        Indexes().ShouldNotContain(
            i => i.Properties.Any(p => p.Name == nameof(AuditEntry.TenantId)));

        Indexes(a => a.MultiTenant(t => t.FromEntityProperty())).ShouldContain(
            i => i.Properties.Any(p => p.Name == nameof(AuditEntry.TenantId)));
    }

    [Fact]
    public void Creates_no_indexes_beyond_the_key_when_asked_for_none()
        => Indexes(a => a.Indexes(AuditIndexes.None)).ShouldBeEmpty();

    [Fact]
    public void Excludes_the_table_from_migrations_when_another_context_owns_it()
    {
        EntryType().IsTableExcludedFromMigrations().ShouldBeFalse();

        // Without this, every context sharing the table scaffolds a migration creating it, and only
        // the first ever applies cleanly.
        EntryType(a => a.SharedAuditTables()).IsTableExcludedFromMigrations().ShouldBeTrue();
    }

    [Fact]
    public void Puts_the_table_where_it_is_told()
    {
        var entityType = EntryType(a => a.Schema("trail").TableName("Changes"));

        entityType.GetSchema().ShouldBe("trail");
        entityType.GetTableName().ShouldBe("Changes");
    }

    /// <summary>
    ///     The audit entry type as the design-time model sees it.
    /// </summary>
    /// <remarks>
    ///     Design-time rather than runtime, because everything asserted here is DDL — index sort
    ///     order, index method annotations, migration exclusion — and EF strips all of it from the
    ///     read-optimized model it uses at runtime.
    /// </remarks>
    private static IEntityType EntryType(Action<AuditOptionsBuilder>? configure = null)
    {
        using var context = TestModel.Context(configure, b => b.Entity<Order>().IsAudited());

        return context.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Single(e => typeof(AuditEntry).IsAssignableFrom(e.ClrType));
    }

    private static List<IIndex> Indexes(Action<AuditOptionsBuilder>? configure = null)
        => [.. EntryType(configure).GetIndexes()];
}
