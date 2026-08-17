using EFToolkit.Bulk.Execution;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Execution;

/// <summary>
///     Turning what the database returned into what the model declares.
/// </summary>
/// <remarks>
///     Reconciliation still has real work to do — a store widens a key, a value converter has to run
///     backwards — but it is no longer where a type mismatch is discovered, because the read asks
///     for the declared type in the first place. What is left here is the narrow set of conversions
///     that are genuinely conversions, and a clear account of the ones that are not.
/// </remarks>
public class BulkColumnInfoTests
{
    [Fact]
    public void Keeps_a_value_that_is_already_the_declared_type()
    {
        var recorded = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

        ColumnModel.Column("RecordedAt").FromProviderValue(recorded).ShouldBe(recorded);
    }

    [Fact]
    public void Narrows_a_value_the_store_widened()
    {
        // A bigint from a sequence feeding an int key, which is the shape this reconciliation
        // exists for.
        ColumnModel.Column("Id").FromProviderValue(7L).ShouldBe(7);
    }

    [Fact]
    public void Runs_the_value_converter_backwards()
        => ColumnModel.Column("Code").FromProviderValue("EARLY")
            .ShouldBe(new ColumnModel.ShiftCode("EARLY"));

    [Fact]
    public void Turns_a_number_into_the_enum_the_property_declares()
        => ColumnModel.Column("Grade").FromProviderValue(1).ShouldBe(ColumnModel.Grade.Premium);

    [Fact]
    public void Leaves_a_null_null()
        => ColumnModel.Column("Note").FromProviderValue(null).ShouldBeNull();

    [Fact]
    public void Explains_a_value_that_cannot_be_reconciled_with_the_property()
    {
        var column = ColumnModel.Column("RecordedAt");

        // What a driver hands back for this column when it reads it as its own default type. The
        // framework's own answer here is "Object must implement IConvertible", which names neither
        // the column nor the property nor the operation that was under way.
        var exception = Should.Throw<BulkNotSupportedException>(
            () => column.FromProviderValue(new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc)));

        exception.Message.ShouldContain("RecordedAt");
        exception.Message.ShouldContain(nameof(ColumnModel.Shift));
        exception.Message.ShouldContain("System.DateTime");
        exception.Message.ShouldContain("System.DateTimeOffset");

        // Namespace-qualified wherever a type is named, the advice included: the whole point is to
        // be read by someone whose model has a Duration or a DateTime of its own.
        exception.Message.ShouldNotContain("'DateTime'");
        exception.Message.ShouldNotContain("'DateTimeOffset'");

        // The cause is kept, so anything reading the exception chain still sees the original.
        exception.InnerException.ShouldBeOfType<InvalidCastException>();
    }

    [Fact]
    public void Reports_the_provider_type_a_converted_property_stores()
        => ColumnModel.Column("Code").ProviderClrType.ShouldBe(typeof(string));

    [Fact]
    public void Reports_the_declared_type_of_a_property_with_no_converter()
        => ColumnModel.Column("RecordedAt").ProviderClrType.ShouldBe(typeof(DateTimeOffset));

    [Fact]
    public void Reports_the_number_an_enum_property_is_stored_as()
    {
        // EF maps an enum through a converter to its underlying type, so the type to ask a driver
        // for is that number -- asking for the enum itself is what no driver would answer.
        ColumnModel.Column("Grade").ProviderClrType.ShouldBe(typeof(int));
    }

    [Fact]
    public void Reports_a_nullable_property_as_the_type_underneath()
    {
        // Nullability is expressed by a null value, so asking a driver for "string?" would be
        // asking for something it has no notion of.
        ColumnModel.Column("Note").ProviderClrType.ShouldBe(typeof(string));
    }
}
