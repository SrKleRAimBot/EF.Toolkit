using System.Text.Json;
using EFToolkit.Audit.Api;
using EFToolkit.Audit.Capture;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Tests.Capture;

public class AuditPayloadWriterTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoMetadata
        = new Dictionary<string, object?>();

    [Fact]
    public void An_insert_records_every_captured_value()
    {
        var payload = Write(AuditOperation.Insert, source => source.Row(1, "REF-1", "Draft", 9.99m));

        payload.GetProperty("op").GetString().ShouldBe("insert");
        payload.GetProperty("new").GetProperty("Reference").GetString().ShouldBe("REF-1");
        payload.GetProperty("key").GetProperty("Id").GetInt32().ShouldBe(1);
        payload.TryGetProperty("old", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_delete_records_the_row_and_no_new_values()
    {
        var payload = Write(AuditOperation.Delete, source => source.Row(1, "REF-1", "Draft", 9.99m));

        payload.GetProperty("op").GetString().ShouldBe("delete");
        payload.GetProperty("old").GetProperty("Reference").GetString().ShouldBe("REF-1");
        payload.TryGetProperty("new", out _).ShouldBeFalse();
    }

    [Fact]
    public void An_update_records_only_what_changed()
    {
        var payload = Write(
            AuditOperation.Update,
            source => source.Changed(
                [1, "REF-1", "Draft", 9.99m],
                [1, "REF-1", "Live", 9.99m]));

        var changed = payload.GetProperty("changed").EnumerateArray()
            .Select(e => e.GetString()).ToList();

        changed.ShouldHaveSingleItem().ShouldBe("Status");
        payload.GetProperty("old").GetProperty("Status").GetString().ShouldBe("Draft");
        payload.GetProperty("new").GetProperty("Status").GetString().ShouldBe("Live");

        // A column that did not move has no business being in either half.
        payload.GetProperty("old").TryGetProperty("Reference", out _).ShouldBeFalse();
    }

    [Fact]
    public void An_update_that_changed_nothing_produces_no_payload()
    {
        // EF marks a property modified when it is assigned at all, including when it is assigned
        // the value it already held. Trusting that would fill the trail with entries recording
        // nothing.
        var payload = WriteOrNull(
            AuditOperation.Update,
            source => source.Changed(
                [1, "REF-1", "Draft", 9.99m],
                [1, "REF-1", "Draft", 9.99m]));

        payload.ShouldBeNull();
    }

    [Fact]
    public void An_update_with_no_before_image_says_so()
    {
        var payload = Write(AuditOperation.Update, source => source.Row(1, "REF-1", "Live", 9.99m));

        // Presenting the new values as though they were both halves would be a lie, and a
        // convincing one.
        payload.GetProperty("partial").GetBoolean().ShouldBeTrue();
        payload.TryGetProperty("old", out _).ShouldBeFalse();
        payload.GetProperty("new").GetProperty("Status").GetString().ShouldBe("Live");
    }

    [Fact]
    public void A_masked_value_is_replaced_and_a_null_one_stays_null()
    {
        var payload = WriteWithSensitive(cardNumber: "4111111111111111");
        payload.GetProperty("new").GetProperty("CardNumber").GetString().ShouldBe("***");

        var empty = WriteWithSensitive(cardNumber: null);

        // "***" where there was no value would say a secret is set when none is.
        empty.GetProperty("new").GetProperty("CardNumber").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void An_excluded_property_never_appears()
    {
        var payload = WriteWithSensitive(cardNumber: null, notes: "do not ship");

        payload.GetProperty("new").TryGetProperty("InternalNotes", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_long_value_is_truncated_and_the_entry_says_so()
    {
        var payload = Write(
            AuditOperation.Insert,
            source => source.Row(1, new string('x', 200), "Draft", 9.99m),
            options: AuditOptions.Default with { MaxValueLength = 16 });

        payload.GetProperty("new").GetProperty("Reference").GetString()!.Length.ShouldBe(16);
        payload.GetProperty("__truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Ambient_metadata_and_the_reason_are_merged_in()
    {
        var payload = Write(
            AuditOperation.Insert,
            source => source.Row(1, "REF-1", "Draft", 9.99m),
            metadata: new Dictionary<string, object?> { ["batch"] = 7, ["run"] = "a" },
            reason: "quarter end");

        var meta = payload.GetProperty("meta");
        meta.GetProperty("reason").GetString().ShouldBe("quarter end");
        meta.GetProperty("batch").GetInt32().ShouldBe(7);
        meta.GetProperty("run").GetString().ShouldBe("a");
    }

    [Fact]
    public void Value_keys_are_ordered_so_two_write_paths_agree_textually()
    {
        var payload = Write(AuditOperation.Insert, source => source.Row(1, "REF-1", "Draft", 9.99m));

        var names = payload.GetProperty("new").EnumerateObject().Select(p => p.Name).ToList();

        // Two sources describing the same change supply their columns in whatever order they
        // happen to hold them. Sorting is what makes the resulting payloads comparable at all.
        names.ShouldBe([.. names.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void Column_names_can_be_used_instead_of_property_names()
    {
        var payload = Write(
            AuditOperation.Insert,
            source => source.Row(1, "REF-1", "Draft", 9.99m),
            configureAuditing: a => a.PayloadNames(AuditPayloadNames.Column),
            configureModel: b => b.Entity<Order>().Property(o => o.Reference).HasColumnName("ref"));

        payload.GetProperty("new").TryGetProperty("ref", out _).ShouldBeTrue();
        payload.GetProperty("new").TryGetProperty("Reference", out _).ShouldBeFalse();
    }

    private static JsonElement WriteWithSensitive(string? cardNumber, string? notes = null)
    {
        using var context = TestModel.Context(onModelCreating: Model);
        var entityType = context.Model.FindEntityType(typeof(Order))!;
        var options = context.Options();
        var plan = AuditEntityPlan.For(entityType, options);

        var properties = new[]
        {
            entityType.FindProperty(nameof(Order.Id))!,
            entityType.FindProperty(nameof(Order.CardNumber))!,
            entityType.FindProperty(nameof(Order.InternalNotes))!,
        };

        var source = new FakeCaptureSource(entityType, AuditOperation.Insert, properties)
            .Row(1, cardNumber, notes);

        using var writer = new AuditPayloadWriter(options);

        return JsonDocument.Parse(
            writer.Write(
                AuditOperation.Insert,
                AuditSourceProjection.Create(source, plan),
                source,
                0,
                NoMetadata,
                reason: null)!).RootElement;
    }

    private static JsonElement Write(
        AuditOperation operation,
        Func<FakeCaptureSource, FakeCaptureSource> rows,
        AuditOptions? options = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        string? reason = null,
        Action<ModelBuilder>? configureModel = null,
        Action<AuditOptionsBuilder>? configureAuditing = null)
        => JsonDocument.Parse(
            WriteOrNull(operation, rows, options, metadata, reason, configureModel, configureAuditing)
                .ShouldNotBeNull()).RootElement;

    private static string? WriteOrNull(
        AuditOperation operation,
        Func<FakeCaptureSource, FakeCaptureSource> rows,
        AuditOptions? options = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        string? reason = null,
        Action<ModelBuilder>? configureModel = null,
        Action<AuditOptionsBuilder>? configureAuditing = null)
    {
        using var context = TestModel.Context(
            configure: configureAuditing,
            onModelCreating: b =>
            {
                Model(b);
                configureModel?.Invoke(b);
            });

        var entityType = context.Model.FindEntityType(typeof(Order))!;
        var settings = options ?? context.Options();
        var plan = AuditEntityPlan.For(entityType, settings);

        var properties = new[]
        {
            entityType.FindProperty(nameof(Order.Id))!,
            entityType.FindProperty(nameof(Order.Reference))!,
            entityType.FindProperty(nameof(Order.Status))!,
            entityType.FindProperty(nameof(Order.Total))!,
        };

        var source = rows(new FakeCaptureSource(entityType, operation, properties));

        using var writer = new AuditPayloadWriter(settings);

        return writer.Write(
            operation, AuditSourceProjection.Create(source, plan), source, 0,
            metadata ?? NoMetadata, reason);
    }

    private static void Model(ModelBuilder builder)
        => builder.Entity<Order>().IsAudited(a => a
            .Exclude(o => o.InternalNotes)
            .Mask(o => o.CardNumber));
}
