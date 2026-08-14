using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EFToolkit.Audit.Infrastructure;

/// <summary>
///     Adds the audit table to the model.
/// </summary>
/// <remarks>
///     Runs after <c>OnModelCreating</c>, so the application never has to mention the audit entry
///     type — <c>UseAuditing()</c> is the whole integration, the same way <c>UseBulkOperations()</c>
///     is for EF.Toolkit.Bulk.
/// </remarks>
internal static class AuditModelBuilder
{
    /// <summary>Column name of the entry's key.</summary>
    public const string IdColumn = "Id";

    /// <summary>Adds and configures the audit entry type.</summary>
    /// <param name="modelBuilder">The model being built.</param>
    /// <param name="options">The context's auditing settings.</param>
    public static void Apply(ModelBuilder modelBuilder, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(options);

        var clrType = typeof(AuditEntry<>).MakeGenericType(options.KeyType);
        var builder = modelBuilder.Entity(clrType);
        var types = options.StoreTypes;

        builder.ToTable(options.TableName, options.Schema, table =>
        {
            if (options.SharedAuditTables)
            {
                // Another context owns the DDL. Without this, every context sharing the table would
                // scaffold a migration creating it, and only the first would ever apply cleanly.
                table.ExcludeFromMigrations();
            }

            if (types.JsonCheck is { } check)
            {
                table.HasCheckConstraint(
                    $"CK_{options.TableName}_Changes_Json",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, check, "Changes"));
            }
        });

        builder.HasKey(IdColumn);

        var id = builder.Property(IdColumn);
        if (options.StoreGeneratedIds)
        {
            id.ValueGeneratedOnAdd();
        }
        else
        {
            // The application supplies the key, and saying so is what keeps a bulk insert of
            // entries from having to read anything back.
            id.ValueGeneratedNever();
        }

        Text(builder, nameof(AuditEntry.EntityType), types, required: true);
        Text(builder, nameof(AuditEntry.EntityKey), types, required: true);
        Text(builder, nameof(AuditEntry.Source), types, required: true);
        Text(builder, nameof(AuditEntry.ActorId), types, required: false);
        Text(builder, nameof(AuditEntry.ActorName), types, required: false);
        Text(builder, nameof(AuditEntry.ActorType), types, required: false);
        Text(builder, nameof(AuditEntry.TenantId), types, required: false);

        builder.Property(nameof(AuditEntry.Operation)).IsRequired();
        builder.Property(nameof(AuditEntry.CorrelationId));

        var occurredAt = builder.Property(nameof(AuditEntry.OccurredAt)).IsRequired();
        if (types.Timestamp is { } timestamp)
        {
            occurredAt.HasColumnType(timestamp);
        }

        var changes = builder.Property(nameof(AuditEntry.Changes)).IsRequired();
        if (types.Json is { } json)
        {
            changes.HasColumnType(json);
        }

        Indexes(builder, options);
        JsonPathIndexes(builder, options);
    }

    private static void Text(
        EntityTypeBuilder builder,
        string property,
        AuditStoreTypes types,
        bool required)
    {
        var text = builder.Property(property).IsRequired(required);

        if (types.Text is { } storeType)
        {
            text.HasColumnType(storeType);
        }
    }

    private static void Indexes(EntityTypeBuilder builder, AuditOptions options)
    {
        if (options.Indexes.HasFlag(AuditIndexes.History))
        {
            // The dominant query: everything that ever happened to one row, newest first.
            builder
                .HasIndex(
                    nameof(AuditEntry.EntityType),
                    nameof(AuditEntry.EntityKey),
                    nameof(AuditEntry.OccurredAt))
                .IsDescending(false, false, true);
        }

        if (options.Indexes.HasFlag(AuditIndexes.Tenant) && options.IsMultiTenant)
        {
            builder
                .HasIndex(nameof(AuditEntry.TenantId), nameof(AuditEntry.OccurredAt))
                .IsDescending(false, true);
        }

        if (options.Indexes.HasFlag(AuditIndexes.Actor))
        {
            builder
                .HasIndex(nameof(AuditEntry.ActorId), nameof(AuditEntry.OccurredAt))
                .IsDescending(false, true);
        }

        if (options.Indexes.HasFlag(AuditIndexes.Correlation))
        {
            builder.HasIndex(nameof(AuditEntry.CorrelationId));
        }

        if (options.Indexes.HasFlag(AuditIndexes.Payload)
            && options.StoreTypes.PayloadIndex is { } annotations)
        {
            var index = builder.HasIndex(nameof(AuditEntry.Changes));

            foreach (var (name, value) in annotations)
            {
                index.HasAnnotation(name, value);
            }
        }
    }

    private static void JsonPathIndexes(EntityTypeBuilder builder, AuditOptions options)
    {
        if (options.StoreTypes.JsonPathIndexes is not { Count: > 0 } paths)
        {
            return;
        }

        foreach (var path in paths)
        {
            var property = builder
                .Property<string>(path.Name)
                .HasComputedColumnSql(path.ComputedSql, stored: true);

            if (path.StoreType is { } storeType)
            {
                property.HasColumnType(storeType);
            }

            builder.HasIndex(path.Name);
        }
    }
}
