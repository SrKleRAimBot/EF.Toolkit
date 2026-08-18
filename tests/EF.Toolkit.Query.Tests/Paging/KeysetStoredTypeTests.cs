using System.Buffers.Text;
using System.Text;
using EFToolkit.Query.Paging;
using EFToolkit.Query.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace EFToolkit.Query.Tests.Paging;

/// <summary>
///     Covers the columns whose CLR type says nothing useful about what a cursor has to carry: an id
///     stored through a value converter, and a type the provider maps natively.
/// </summary>
/// <remarks>
///     The rule these all turn on is that a cursor carries the <em>stored</em> value. Deciding that
///     from the CLR type is what used to refuse a strongly typed id stored as <c>text</c> — a column
///     that orders, compares and round-trips perfectly well — before any model was in sight.
/// </remarks>
public class KeysetStoredTypeTests
{
    private static readonly Instant Hired = Instant.FromUtc(2026, 3, 1, 9, 30) + Duration.FromNanoseconds(123456789);

    [Fact]
    public void A_value_converted_id_can_be_paged_along()
    {
        using var context = StoredTypeModel.Context();
        var keys = KeysetDefinition.For<Worker>(k => k.Ascending(w => w.Id).AllowConvertedKey());

        Should.NotThrow(() => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Worker))));
    }

    [Fact]
    public void A_value_converted_id_still_needs_the_caller_to_vouch_for_the_conversion()
    {
        // Unchanged, and the reason the two questions are separate: whether the cursor can carry the
        // stored value is the model's to answer, whether the conversion sorts the same way is not.
        using var context = StoredTypeModel.Context();
        var keys = KeysetDefinition.For<Worker>(k => k.Ascending(w => w.Id));

        Should.Throw<QueryNotSupportedException>(
                () => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Worker))))
            .Message.ShouldContain("value converter");
    }

    [Fact]
    public void A_cursor_over_a_converted_id_carries_the_stored_value()
    {
        using var context = StoredTypeModel.Context();
        var keys = KeysetDefinition.For<Worker>(k => k.Ascending(w => w.Id).AllowConvertedKey());
        var binding = keys.ValidateAgainst(context.Model.FindEntityType(typeof(Worker)));

        var row = new Worker { Id = new WorkerId("w-0042") };
        var cursor = keys.CursorFor(row, KeysetPageDirection.Forward, binding);

        // What ORDER BY and the page comparison both run against is the text in the column, so that
        // is what the token holds — not a rendering of the CLR type over the top of it.
        Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor.Token)).ShouldEndWith("w-0042");
        keys.Decode(cursor, binding).ShouldBe([row.Id]);
    }

    [Fact]
    public void A_boundary_over_a_converted_id_compares_against_the_column()
    {
        using var context = StoredTypeModel.Context();
        var keys = KeysetDefinition.For<Worker>(k => k.Ascending(w => w.Id).AllowConvertedKey());
        var binding = keys.ValidateAgainst(context.Model.FindEntityType(typeof(Worker)));

        var cursor = keys.CursorFor(
            new Worker { Id = new WorkerId("w-0042") },
            KeysetPageDirection.Forward,
            binding);

        var sql = context.Workers.Where(keys.After(cursor, binding)).ToQueryString();

        // WorkerId defines no comparison operators and no IComparable — the ordering asked for is the
        // one the database applies to the text it is stored as, and that is what reaches the SQL.
        sql.ShouldContain("\"Id\" > ");
        sql.ShouldContain("w-0042");
    }

    [Fact]
    public void A_tie_on_a_converted_id_compares_as_equality_against_the_column()
    {
        // The lexicographic expansion needs equality on every leading component too, and a record
        // struct's == is no more translatable than its (missing) > would have been.
        using var context = StoredTypeModel.Context();
        var keys = KeysetDefinition.For<Worker>(k => k
            .Ascending(w => w.Id)
            .Ascending(w => w.HiredAt)
            .AllowConvertedKey()
            .AllowNonUniqueKey());

        var binding = keys.ValidateAgainst(context.Model.FindEntityType(typeof(Worker)));

        var cursor = keys.CursorFor(
            new Worker { Id = new WorkerId("w-0042"), HiredAt = Hired },
            KeysetPageDirection.Forward,
            binding);

        var sql = context.Workers.Where(keys.After(cursor, binding)).ToQueryString();

        sql.ShouldContain("\"Id\" = ");
        sql.ShouldContain("\"HiredAt\" > ");
    }

    [Theory]
    [InlineData(nameof(Worker.HiredAt))]
    [InlineData(nameof(Worker.StartsOn))]
    public void A_column_the_provider_maps_natively_can_be_paged_along(string column)
    {
        // Neither type has a value converter — the Npgsql plugin maps Instant to timestamptz and
        // LocalDate to date directly — so the only thing that ever refused them was the cursor.
        using var context = StoredTypeModel.Context();

        var keys = column == nameof(Worker.HiredAt)
            ? KeysetDefinition.For<Worker>(k => k.Ascending(w => w.HiredAt).AllowNonUniqueKey())
            : KeysetDefinition.For<Worker>(k => k.Ascending(w => w.StartsOn).AllowNonUniqueKey());

        Should.NotThrow(() => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Worker))));
    }

    [Fact]
    public void A_natively_mapped_boundary_survives_the_trip_at_full_precision()
    {
        using var context = StoredTypeModel.Context();
        var keys = KeysetDefinition.For<Worker>(k => k
            .Ascending(w => w.HiredAt)
            .Ascending(w => w.Id)
            .AllowConvertedKey());

        var binding = keys.ValidateAgainst(context.Model.FindEntityType(typeof(Worker)));
        var row = new Worker { Id = new WorkerId("w-1"), HiredAt = Hired };

        var cursor = KeysetCursor.Parse(
            keys.CursorFor(row, KeysetPageDirection.Forward, binding).Token);

        // Down to the nanosecond: Instant's own ToString truncates to the second, and a boundary
        // rounded down repeats every row inside the second it points at.
        keys.Decode(cursor, binding).ShouldBe([Hired, row.Id]);
    }

    [Fact]
    public void A_NodaTime_value_round_trips_through_the_codec()
    {
        AssertRoundTrip(Hired);
        AssertRoundTrip(new LocalDate(2026, 3, 1));
        AssertRoundTrip(new LocalTime(9, 30, 0).PlusNanoseconds(123456789));
        AssertRoundTrip(new LocalDateTime(2026, 3, 1, 9, 30, 0).PlusNanoseconds(123456789));

        static void AssertRoundTrip<T>(T value)
            where T : notnull
            => KeysetValueCodec.Decode(KeysetValueCodec.Encode(value, typeof(T)), typeof(T))
                .ShouldBe(value);
    }

    [Fact]
    public void A_type_a_cursor_cannot_carry_is_refused_once_the_model_says_how_it_is_stored()
    {
        // Nothing maps Rank, so the cursor would have to carry it as itself — and it has neither a
        // form the codec knows nor a TypeConverter of its own. The refusal names what it is stored as.
        var keys = KeysetDefinition.For<WorkerSummary>(k => k.Ascending(w => w.Rank));

        Should.Throw<QueryNotSupportedException>(() => keys.ValidateAgainst(null))
            .Message.ShouldContain("round-trippable text form");
    }

    [Fact]
    public void A_TypeConverter_that_does_not_read_its_own_output_back_is_refused()
    {
        // Metres renders 1.5 as "1". Left alone, the cursor would point one row short of where the
        // page ended and the row on the boundary would come back twice.
        var keys = KeysetDefinition.For<WorkerSummary>(k => k.Ascending(w => w.Distance));
        var binding = keys.ValidateAgainst(null);

        Should.Throw<QueryNotSupportedException>(() => keys.CursorFor(
                new WorkerSummary { Distance = new Metres(1.5m) },
                KeysetPageDirection.Forward,
                binding))
            .Message.ShouldContain("does not round-trip");
    }
}
