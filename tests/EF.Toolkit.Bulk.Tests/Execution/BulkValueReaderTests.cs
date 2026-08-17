using EFToolkit.Bulk.Execution;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Execution;

/// <summary>
///     How a field is read when the driver's default CLR type for it is not the model's.
/// </summary>
/// <remarks>
///     <para>
///         The case that made this necessary: a before-image read that took whatever
///         <c>GetValue</c> returned and tried to reconcile it afterwards with
///         <c>Convert.ChangeType</c>. That works only where both types cooperate, and the pairs a
///         driver actually produces mostly do not — <c>Instant</c> for a <c>Duration</c> property,
///         <c>DateTime</c> for a <c>DateTimeOffset</c> one — so an audited bulk update of such an
///         entity failed outright.
///     </para>
///     <para>
///         Each test uses a distinct field-type/target-type pair, because a refusal is remembered
///         per pair for the life of the process and shared pairs would make the tests depend on the
///         order they ran in.
///     </para>
/// </remarks>
public class BulkValueReaderTests
{
    [Fact]
    public void Asks_the_driver_for_the_type_the_property_declares()
    {
        var expected = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

        // What Npgsql does with timestamptz: the field reads as a DateTime unless another type is
        // asked for, and DateTime converts to nothing.
        var reader = Row(new StubField(
            "RecordedAt",
            typeof(DateTime),
            expected.UtcDateTime,
            _ => expected));

        var value = BulkValueReader.Read(reader, 0, ColumnModel.Column("RecordedAt"));

        value.ShouldBe(expected);
        reader.TypedReads.ShouldBe([typeof(DateTimeOffset)]);
    }

    [Fact]
    public void Takes_the_plain_value_when_the_driver_already_returns_the_declared_type()
    {
        var expected = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

        var reader = Row(new StubField("RecordedAt", typeof(DateTimeOffset), expected));

        BulkValueReader.Read(reader, 0, ColumnModel.Column("RecordedAt")).ShouldBe(expected);

        // Nothing to convert, so nothing is asked of the driver -- which is the common case and
        // must stay as cheap as it was.
        reader.TypedReads.ShouldBeEmpty();
    }

    [Fact]
    public void Falls_back_to_the_plain_value_when_the_driver_refuses_the_type()
    {
        // A widened key: the store hands back a bigint for an int column and declines to narrow it
        // itself. Reconciling afterwards is exactly right here, and is what still happens.
        var reader = Row(new StubField("Id", typeof(long), 7L));
        var column = ColumnModel.Column("Id");

        var value = BulkValueReader.Read(reader, 0, column);

        value.ShouldBe(7L);
        column.FromProviderValue(value).ShouldBe(7);
    }

    [Fact]
    public void Asks_a_refusing_driver_only_once_for_the_same_shape()
    {
        var reader = Row(new StubField("Date", typeof(TimeSpan), TimeSpan.FromDays(1)));
        var column = ColumnModel.Column("Date");

        for (var i = 0; i < 5; i++)
        {
            BulkValueReader.Read(reader, 0, column).ShouldBe(TimeSpan.FromDays(1));
        }

        // A refusal costs one exception per shape, not one per row: a batch is thousands of rows
        // wide and each would otherwise throw and catch.
        reader.TypedReads.Count.ShouldBe(1);
    }

    [Fact]
    public void Reads_an_enum_property_as_the_number_it_is_stored_as()
    {
        var reader = Row(new StubField("Grade", typeof(int), 1));
        var column = ColumnModel.Column("Grade");

        var value = BulkValueReader.Read(reader, 0, column);

        // EF stores the enum as a number through a converter, so the number is what the driver is
        // holding and what the converter expects back. Nothing to ask for.
        reader.TypedReads.ShouldBeEmpty();
        column.FromProviderValue(value).ShouldBe(ColumnModel.Grade.Premium);
    }

    [Fact]
    public void Asks_for_the_provider_type_of_a_converted_property()
    {
        var reader = Row(new StubField("Code", typeof(object), "EARLY", _ => "EARLY"));
        var column = ColumnModel.Column("Code");

        var value = BulkValueReader.Read(reader, 0, column);

        // The converter's provider type, not the property's own: the value converter runs after
        // this, and it expects what it wrote.
        reader.TypedReads.ShouldBe([typeof(string)]);
        column.FromProviderValue(value).ShouldBe(new ColumnModel.ShiftCode("EARLY"));
    }

    [Fact]
    public void Takes_the_plain_value_for_a_column_the_model_does_not_describe()
    {
        var reader = Row(new StubField("anything", typeof(DateTime), DateTime.UnixEpoch));

        var column = new BulkColumnInfo(
            "anything", typeMapping: null, property: null, isWrite: false, isRead: true,
            isKey: false);

        BulkValueReader.Read(reader, 0, column).ShouldBe(DateTime.UnixEpoch);
        reader.TypedReads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reads_a_database_null_as_null()
    {
        var reader = Row(new StubField("Note", typeof(string), Value: null));

        var value = await BulkValueReader.ReadAsync(
            reader, 0, ColumnModel.Column("Note"), TestContext.Current.CancellationToken);

        value.ShouldBeNull();
        reader.TypedReads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reads_a_value_the_same_way_whether_or_not_the_null_check_comes_first()
    {
        var expected = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

        var reader = Row(new StubField(
            "RecordedAt", typeof(DateTime), expected.UtcDateTime, _ => expected));

        var value = await BulkValueReader.ReadAsync(
            reader, 0, ColumnModel.Column("RecordedAt"), TestContext.Current.CancellationToken);

        value.ShouldBe(expected);
    }

    private static StubDataReader Row(params StubField[] fields) => new(fields);
}
