using System.Text;
using EFToolkit.Query.Paging;
using EFToolkit.Query.Tests.Infrastructure;

namespace EFToolkit.Query.Tests.Paging;

/// <summary>Covers the cursor's encoding, and every way a cursor can arrive back wrong.</summary>
public class KeysetCursorTests
{
    private static KeysetDefinition<Order> ByPlacedThenId() => KeysetDefinition.For<Order>(k => k
        .Descending(o => o.PlacedAt)
        .Ascending(o => o.Id));

    [Fact]
    public void A_cursor_round_trips_through_its_token()
    {
        var keys = ByPlacedThenId();
        var row = new Order { Id = 42, PlacedAt = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc) };

        var original = keys.CursorFor(row, KeysetPageDirection.Forward);
        var parsed = KeysetCursor.Parse(original.Token);

        parsed.Direction.ShouldBe(KeysetPageDirection.Forward);
        keys.Decode(parsed).ShouldBe([row.PlacedAt, row.Id]);
    }

    [Fact]
    public void A_backward_cursor_says_so()
    {
        var keys = ByPlacedThenId();
        var cursor = keys.CursorFor(new Order { Id = 1 }, KeysetPageDirection.Backward);

        KeysetCursor.Parse(cursor.Token).Direction.ShouldBe(KeysetPageDirection.Backward);
    }

    [Theory]
    [InlineData(typeof(int), 42)]
    [InlineData(typeof(long), 9_000_000_000L)]
    [InlineData(typeof(short), (short)7)]
    [InlineData(typeof(byte), (byte)200)]
    [InlineData(typeof(bool), true)]
    [InlineData(typeof(string), "Smith|Jones & Co %_")]
    [InlineData(typeof(double), 1.7976931348623157E+308)]
    [InlineData(typeof(float), 3.4028235E+38f)]
    [InlineData(typeof(char), 'x')]
    public void Every_supported_value_type_survives_the_trip(Type type, object value)
        => KeysetValueCodec.Decode(KeysetValueCodec.Encode(value, type), type).ShouldBe(value);

    [Fact]
    public void Values_that_cannot_be_written_as_inline_data_survive_the_trip()
    {
        AssertRoundTrip(decimal.MaxValue);
        AssertRoundTrip(new DateTime(2026, 3, 1, 9, 30, 0, 123, DateTimeKind.Utc).AddTicks(4567));
        AssertRoundTrip(new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.FromHours(-5)));
        AssertRoundTrip(new DateOnly(2026, 3, 1));
        AssertRoundTrip(new TimeOnly(9, 30, 0, 123));
        AssertRoundTrip(TimeSpan.FromTicks(123456789));
        AssertRoundTrip(Guid.NewGuid());
        AssertRoundTrip(OrderStatus.Cancelled);

        // Compared by content: two distinct arrays are never equal by reference.
        var bytes = new byte[] { 1, 2, 250, 0, 255 };
        ((byte[])KeysetValueCodec.Decode(KeysetValueCodec.Encode(bytes, typeof(byte[])), typeof(byte[])))
            .ShouldBe(bytes);

        static void AssertRoundTrip<T>(T value)
            where T : notnull
            => KeysetValueCodec.Decode(KeysetValueCodec.Encode(value, typeof(T)), typeof(T))
                .ShouldBe(value);
    }

    [Fact]
    public void A_separator_inside_a_value_does_not_split_the_cursor()
    {
        // Values are escaped one at a time rather than the payload as a whole, so a name containing
        // the separator cannot decode as two components and shift every later value by one column.
        var keys = KeysetDefinition.For<Order>(k => k
            .Ascending(o => o.Reference)
            .Ascending(o => o.Id));

        var row = new Order { Id = 3, Reference = "a|b|c" };
        var parsed = KeysetCursor.Parse(keys.CursorFor(row, KeysetPageDirection.Forward).Token);

        keys.Decode(parsed).ShouldBe(["a|b|c", 3]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64url!!")]
    [InlineData("YWJj")]
    public void A_malformed_token_is_rejected_without_throwing(string? token)
    {
        KeysetCursor.TryParse(token, out var cursor, out var error).ShouldBeFalse();
        cursor.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Parse_throws_where_TryParse_reports()
        => Should.Throw<QueryNotSupportedException>(() => KeysetCursor.Parse("not-a-cursor"));

    [Fact]
    public void A_token_from_a_future_version_is_rejected_by_name()
    {
        var token = Token("2|abcdef01|f|1");

        KeysetCursor.TryParse(token, out _, out var error).ShouldBeFalse();
        error.ShouldContain("version");
    }

    [Fact]
    public void A_token_with_an_unrecognised_direction_is_rejected()
    {
        KeysetCursor.TryParse(Token("1|abcdef01|sideways|1"), out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_cursor_from_a_different_ordering_is_refused_rather_than_answered()
    {
        // Replayed against the wrong ordering the boundary values line up with the wrong columns, and
        // the page that comes back is arbitrary rather than empty — so this has to be loud.
        var issued = ByPlacedThenId().CursorFor(new Order { Id = 1 }, KeysetPageDirection.Forward);

        var otherOrdering = KeysetDefinition.For<Order>(k => k
            .Ascending(o => o.Total)
            .Ascending(o => o.Id));

        var failure = Should.Throw<QueryNotSupportedException>(() => otherOrdering.Decode(issued));
        failure.Message.ShouldContain("different ordering");
    }

    [Fact]
    public void Reversing_a_component_direction_changes_the_fingerprint()
    {
        var ascending = KeysetDefinition.For<Order>(k => k.Ascending(o => o.Id));
        var descending = KeysetDefinition.For<Order>(k => k.Descending(o => o.Id));

        descending.Fingerprint.ShouldNotBe(ascending.Fingerprint);
    }

    [Fact]
    public void The_same_ordering_fingerprints_the_same_every_time()
        => ByPlacedThenId().Fingerprint.ShouldBe(ByPlacedThenId().Fingerprint);

    [Fact]
    public void A_cursor_carrying_the_wrong_number_of_values_is_refused()
    {
        var keys = ByPlacedThenId();
        var truncated = new KeysetCursor(keys.Fingerprint, KeysetPageDirection.Forward, ["1"]);

        Should.Throw<QueryNotSupportedException>(() => keys.Decode(truncated))
            .Message.ShouldContain("altered in transit");
    }

    [Fact]
    public void A_value_that_is_not_of_the_column_type_is_refused()
    {
        var keys = KeysetDefinition.For<Order>(k => k.Ascending(o => o.Id));
        var tampered = new KeysetCursor(keys.Fingerprint, KeysetPageDirection.Forward, ["not-a-number"]);

        Should.Throw<QueryNotSupportedException>(() => keys.Decode(tampered))
            .Message.ShouldContain("altered in transit");
    }

    [Fact]
    public void A_cursor_renders_as_its_token_so_it_interpolates_into_a_url()
    {
        var cursor = ByPlacedThenId().CursorFor(new Order { Id = 1 }, KeysetPageDirection.Forward);

        cursor.ToString().ShouldBe(cursor.Token);
        cursor.Token.ShouldNotContain("=");
        cursor.Token.ShouldNotContain("+");
        cursor.Token.ShouldNotContain("/");
    }

    private static string Token(string payload)
        => System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
}
