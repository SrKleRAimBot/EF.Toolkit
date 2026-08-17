using System.Globalization;
using System.Text.Json;
using EFToolkit.Audit.Api;
using EFToolkit.Audit.Capture;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Tests.Capture;

/// <summary>
///     Why a decimal is written at its smallest exact scale.
/// </summary>
/// <remarks>
///     <para>
///         A <see cref="decimal" /> carries its scale as part of its representation, so
///         <c>1.5m</c> and <c>1.50m</c> are equal and serialize differently. That only matters
///         because the two capture paths obtain their values from different places: the change
///         tracker holds what the application assigned, while a bulk operation reads a deleted
///         row's before-image back from the store, where the column's declared scale has been
///         applied. The same change then produced <c>1.5</c> through one path and <c>1.50</c>
///         through the other.
///     </para>
///     <para>
///         Byte-identity between the two paths is a guarantee this library makes, and a difference
///         that carries no information is the worst possible reason to break it — an entry diff
///         would report a change to a column nobody touched.
///     </para>
/// </remarks>
public class DecimalScaleTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoMetadata
        = new Dictionary<string, object?>();

    // ---------------------------------------------------------------------------------------
    // What canonicalization does
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("1.500", "1.5")]
    [InlineData("0.00", "0")]
    [InlineData("0.0", "0")]
    [InlineData("100.00", "100")]
    [InlineData("-1.50", "-1.5")]
    [InlineData("-0.00", "0")]
    [InlineData("10.10", "10.1")]
    public void Trailing_zeros_are_stripped(string input, string expected)
        => Render(input).ShouldBe(expected);

    [Theory]
    [InlineData("9.99")]
    [InlineData("1.05")]
    [InlineData("0.001")]
    [InlineData("1.5")]
    [InlineData("100")]
    [InlineData("0")]
    [InlineData("-9.99")]
    public void A_value_with_nothing_to_strip_is_left_alone(string input)
        => Render(input).ShouldBe(input);

    [Fact]
    public void An_interior_zero_survives()
    {
        // 1.05 must not become 1.5. Only zeros at the end carry no information; one in the middle
        // is the value.
        Render("1.0500").ShouldBe("1.05");
    }

    [Theory]
    [InlineData("1.50")]
    [InlineData("0.00")]
    [InlineData("9.99")]
    [InlineData("-1.50")]
    [InlineData("0.0000000000000000000000000001")]
    public void Canonicalizing_never_changes_the_number(string input)
    {
        // The whole justification is that this is a rendering change and not a value change, so
        // the reduced value has to compare equal to what it came from.
        var value = Parse(input);

        AuditValues.Canonical(value).ShouldBe(value);
    }

    [Fact]
    public void The_smallest_representable_value_keeps_every_digit()
    {
        // Scale 28, and every one of those places is load-bearing.
        var value = Parse("0.0000000000000000000000000001");

        AuditValues.Canonical(value).ShouldBe(value);
        Render("0.0000000000000000000000000001").ShouldBe("0.0000000000000000000000000001");
    }

    [Fact]
    public void A_long_run_of_trailing_zeros_reduces_all_the_way()
    {
        Render("1.000000000000000000000000000").ShouldBe("1");
    }

    [Theory]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    public void The_extremes_are_left_alone(string input)
    {
        // decimal.MaxValue and MinValue have scale 0, so there is nothing to reduce and nothing
        // that could overflow on the way.
        Render(input).ShouldBe(input);
    }

    [Fact]
    public void Canonicalizing_is_idempotent()
    {
        var once = AuditValues.Canonical(Parse("1.50"));

        AuditValues.Canonical(once).ShouldBe(once);
        once.ToString(CultureInfo.InvariantCulture).ShouldBe("1.5");
    }

    // ---------------------------------------------------------------------------------------
    // What it is for
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_same_number_at_two_scales_produces_one_payload()
    {
        // The actual guarantee, stated directly: the value the application assigned and the value
        // read back from a numeric(18,2) column describe the same change and must serialize the
        // same way.
        var fromTracker = Payload(Parse("10.5"));
        var fromStore = Payload(Parse("10.50"));

        fromStore.ShouldBe(fromTracker);
    }

    [Fact]
    public void A_zero_from_the_store_matches_a_zero_from_the_tracker()
    {
        // The case the audit equivalence suite hit: a default-valued decimal is 0m on the entity
        // and 0.00 once a numeric(18,2) column has been through it.
        Payload(Parse("0.00")).ShouldBe(Payload(Parse("0")));
    }

    [Fact]
    public void A_decimal_reaches_the_payload_as_a_number_and_not_a_string()
    {
        // Canonicalizing must not quietly turn the column into text, which would be a far bigger
        // change to the payload than the one being fixed.
        var payload = JsonDocument.Parse(Payload(Parse("10.50"))).RootElement;
        var amount = payload.GetProperty("new").GetProperty(nameof(Priced.Amount));

        amount.ValueKind.ShouldBe(JsonValueKind.Number);
        amount.GetDecimal().ShouldBe(10.5m);
    }

    [Fact]
    public void An_update_that_only_changed_scale_reports_no_change()
    {
        // Two images of the same number differing only in scale are not a change, and were already
        // compared through the property's ValueComparer rather than by rendering. Asserted so the
        // two halves cannot drift apart.
        using var context = TestModel.Context(
            a => a.AuditAllEntities(),
            b => b.Entity<Priced>().Property(p => p.Amount).HasPrecision(18, 2));

        var entityType = context.Model.FindEntityType(typeof(Priced))!;
        var options = context.Options();

        var properties = new[]
        {
            entityType.FindProperty(nameof(Priced.Id))!,
            entityType.FindProperty(nameof(Priced.Amount))!,
        };

        using var writer = new AuditPayloadWriter(options);

        var source = new FakeCaptureSource(entityType, AuditOperation.Update, properties)
            .Changed([1, Parse("10.5")], [1, Parse("10.50")]);

        var json = writer.Write(
            AuditOperation.Update,
            AuditSourceProjection.Create(source, AuditEntityPlan.For(entityType, options)),
            source,
            0,
            NoMetadata,
            reason: null);

        // Nothing moved, so there is no entry to write at all.
        json.ShouldBeNull();
    }

    [Fact]
    public void A_decimal_key_renders_the_same_from_either_scale()
    {
        // The key column is text, so the same divergence would put one row's history under two
        // different keys.
        AuditValues.ToKeyText(Parse("10.50")).ShouldBe(AuditValues.ToKeyText(Parse("10.5")));
        AuditValues.ToKeyText(Parse("10.50")).ShouldBe("10.5");
    }

    [Fact]
    public void A_masked_decimal_is_still_masked()
    {
        // Canonicalization happens on the way to the writer, and must not route around masking.
        using var context = TestModel.Context(
            a => a.AuditAllEntities(),
            b => b.Entity<Priced>(e =>
            {
                e.Property(p => p.Amount).HasPrecision(18, 2);
                e.IsAudited(x => x.Mask(p => p.Amount));
            }));

        var entityType = context.Model.FindEntityType(typeof(Priced))!;
        var options = context.Options();

        var properties = new[]
        {
            entityType.FindProperty(nameof(Priced.Id))!,
            entityType.FindProperty(nameof(Priced.Amount))!,
        };

        using var writer = new AuditPayloadWriter(options);

        var source = new FakeCaptureSource(entityType, AuditOperation.Insert, properties)
            .Row(1, Parse("10.50"));

        var payload = JsonDocument.Parse(
            writer.Write(
                AuditOperation.Insert,
                AuditSourceProjection.Create(source, AuditEntityPlan.For(entityType, options)),
                source,
                0,
                NoMetadata,
                reason: null)!).RootElement;

        payload.GetProperty("new").GetProperty(nameof(Priced.Amount)).GetString()
            .ShouldBe(options.MaskToken);
    }

    private static decimal Parse(string value)
        => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static string Render(string input)
        => AuditValues.Canonical(Parse(input)).ToString(CultureInfo.InvariantCulture);

    /// <summary>The payload an insert of <paramref name="amount" /> produces.</summary>
    private static string Payload(decimal amount)
    {
        using var context = TestModel.Context(
            a => a.AuditAllEntities(),
            b => b.Entity<Priced>().Property(p => p.Amount).HasPrecision(18, 2));

        var entityType = context.Model.FindEntityType(typeof(Priced))!;
        var options = context.Options();

        var properties = new[]
        {
            entityType.FindProperty(nameof(Priced.Id))!,
            entityType.FindProperty(nameof(Priced.Amount))!,
        };

        using var writer = new AuditPayloadWriter(options);

        var source = new FakeCaptureSource(entityType, AuditOperation.Insert, properties)
            .Row(1, amount);

        return writer.Write(
            AuditOperation.Insert,
            AuditSourceProjection.Create(source, AuditEntityPlan.For(entityType, options)),
            source,
            0,
            NoMetadata,
            reason: null)!;
    }

    public class Priced
    {
        public int Id { get; set; }

        public decimal Amount { get; set; }
    }
}
