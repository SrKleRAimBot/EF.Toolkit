using EFToolkit.Bulk.Execution;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Execution;

/// <summary>
///     What a captured before-image holds, and for which rows.
/// </summary>
public class BulkBeforeImagesTests
{
    [Fact]
    public void Describes_the_whole_row_rather_than_the_columns_being_written()
    {
        var columns = BulkBeforeImages.ColumnsFor(ColumnModel.EntityType());

        // A delete is the case that settles it: the operation itself knows only the key, and an
        // observer told only the key learns nothing about what was lost.
        columns.Select(c => c.Property!.Name).Order(StringComparer.Ordinal).ShouldBe(
            ["Code", "Date", "Grade", "Id", "Note", "RecordedAt"]);
    }

    [Fact]
    public void Records_a_captured_value_in_the_type_the_model_declares()
    {
        var images = Capture(rowCount: 2);
        var ordinal = Ordinal(images, "RecordedAt");
        var recorded = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

        images.SetValue(0, ordinal, recorded);

        images.HasRow(0).ShouldBeTrue();
        images.GetValue(0, ordinal).ShouldBe(recorded);
    }

    [Fact]
    public void Leaves_a_row_the_target_did_not_hold_unmarked()
    {
        var images = Capture(rowCount: 2);

        images.SetValue(0, Ordinal(images, "Id"), 1);

        // The second row matched nothing, which is how a merge knows it inserted rather than
        // updated it.
        images.HasRow(1).ShouldBeFalse();
        images.GetValue(1, Ordinal(images, "Id")).ShouldBeNull();
    }

    [Fact]
    public void Converts_every_column_of_a_row_the_operation_removed()
    {
        var images = Capture(rowCount: 1);

        var values = new object?[images.Columns.Count];
        values[Ordinal(images, "Id")] = 9L;
        values[Ordinal(images, "Code")] = "EARLY";
        values[Ordinal(images, "Grade")] = 1;

        images.AddRemovedRow(values);

        var removed = images.RemovedRows.ShouldHaveSingleItem();
        removed[Ordinal(images, "Id")].ShouldBe(9);
        removed[Ordinal(images, "Code")].ShouldBe(new ColumnModel.ShiftCode("EARLY"));
        removed[Ordinal(images, "Grade")].ShouldBe(ColumnModel.Grade.Premium);
    }

    [Fact]
    public void Says_which_column_could_not_be_reconciled()
    {
        var images = Capture(rowCount: 1);

        var exception = Should.Throw<BulkNotSupportedException>(
            () => images.SetValue(
                0,
                Ordinal(images, "RecordedAt"),
                new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc)));

        exception.Message.ShouldContain("RecordedAt");
    }

    private static BulkBeforeImages Capture(int rowCount)
        => new(BulkBeforeImages.ColumnsFor(ColumnModel.EntityType()), rowCount);

    private static int Ordinal(BulkBeforeImages images, string property)
    {
        for (var i = 0; i < images.Columns.Count; i++)
        {
            if (images.Columns[i].Property?.Name == property)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"'{property}' is not in the before-image.");
    }
}
