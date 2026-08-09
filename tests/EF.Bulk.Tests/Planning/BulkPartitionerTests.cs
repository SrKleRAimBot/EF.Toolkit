using EFBulk.Configuration;
using EFBulk.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace EFBulk.Tests.Planning;

public class BulkPartitionerTests
{
    private static readonly BulkOptions Eager = BulkOptions.Default with { Threshold = 1 };

    [Fact]
    public void Groups_commands_of_identical_shape_into_one_partition()
    {
        var commands = Many(5, () => FakeCommand.Insert("Customers", "Name", "Email"));

        var partitions = BulkPartitioner.Partition(commands, Eager);

        partitions.Count.ShouldBe(1);
        partitions[0].Commands.Count.ShouldBe(5);
        partitions[0].TableName.ShouldBe("Customers");
        partitions[0].EntityState.ShouldBe(EntityState.Added);
        partitions[0].CanAccelerate.ShouldBeTrue();
    }

    [Fact]
    public void Splits_by_table()
    {
        List<IReadOnlyModificationCommand> commands =
        [
            FakeCommand.Insert("Customers", "Name"),
            FakeCommand.Insert("Orders", "Name"),
            FakeCommand.Insert("Customers", "Name")
        ];

        var partitions = BulkPartitioner.Partition(commands, Eager);

        partitions.Count.ShouldBe(2);
        partitions.Single(p => p.TableName == "Customers").Commands.Count.ShouldBe(2);
        partitions.Single(p => p.TableName == "Orders").Commands.Count.ShouldBe(1);
    }

    [Fact]
    public void Splits_by_entity_state()
    {
        List<IReadOnlyModificationCommand> commands =
        [
            FakeCommand.Insert("Customers", "Name"),
            FakeCommand.Update("Customers", "Name"),
            FakeCommand.Delete("Customers", "Id")
        ];

        var partitions = BulkPartitioner.Partition(commands, Eager);

        partitions.Count.ShouldBe(3);
        partitions.Select(p => p.EntityState)
            .ShouldBe([EntityState.Added, EntityState.Modified, EntityState.Deleted]);
    }

    [Fact]
    public void Splits_by_written_column_set()
    {
        // A partial update touching different columns cannot share a statement with one touching
        // others, even on the same table in the same state.
        List<IReadOnlyModificationCommand> commands =
        [
            FakeCommand.Update("Customers", "Name"),
            FakeCommand.Update("Customers", "Email"),
            FakeCommand.Update("Customers", "Name")
        ];

        var partitions = BulkPartitioner.Partition(commands, Eager);

        partitions.Count.ShouldBe(2);
        partitions[0].Commands.Count.ShouldBe(2);
        partitions[1].Commands.Count.ShouldBe(1);
    }

    [Fact]
    public void Splits_by_written_column_order()
    {
        // A bulk copy writes a positional stream, so the same columns in a different order are a
        // different shape.
        List<IReadOnlyModificationCommand> commands =
        [
            FakeCommand.Insert("Customers", "Name", "Email"),
            FakeCommand.Insert("Customers", "Email", "Name")
        ];

        var partitions = BulkPartitioner.Partition(commands, Eager);

        partitions.Count.ShouldBe(2);
    }

    [Fact]
    public void Splits_by_read_column_set()
    {
        // A command that needs values back takes a different execution path from one that does
        // not, so the two cannot share a statement even with identical writes.
        List<IReadOnlyModificationCommand> commands =
        [
            Shaped(EntityState.Added, Write("Name")),
            Shaped(EntityState.Added, Write("Name"), Read("Id")),
            Shaped(EntityState.Added, Write("Name"), Read("Id"))
        ];

        var partitions = BulkPartitioner.Partition(commands, Eager);

        partitions.Count.ShouldBe(2);
        partitions[0].Commands.Count.ShouldBe(1);
        partitions[1].Commands.Count.ShouldBe(2);
    }

    [Fact]
    public void Splits_by_condition_column_set()
    {
        // The conditions become the join a staged update is applied through, so rows located by
        // different columns cannot be applied together.
        List<IReadOnlyModificationCommand> commands =
        [
            Shaped(EntityState.Modified, Write("Total"), Condition("Id")),
            Shaped(EntityState.Modified, Write("Total"), Condition("Id"), Condition("Version"))
        ];

        BulkPartitioner.Partition(commands, Eager).Count.ShouldBe(2);
    }

    [Fact]
    public void Distinguishes_a_written_column_from_one_that_is_also_a_condition()
    {
        // A client-managed concurrency token is written and matched on at once. Recording it only
        // as a write would merge it with a plain update, which is applied through a different
        // statement -- the kind of shape collision that no value comparison would reveal.
        var token = new FakeColumn { ColumnName = "Version", IsWrite = true, IsCondition = true };

        List<IReadOnlyModificationCommand> commands =
        [
            Shaped(EntityState.Modified, Condition("Id"), Write("Version")),
            Shaped(EntityState.Modified, Condition("Id"), token),
            Shaped(EntityState.Modified, Condition("Id"), token)
        ];

        var partitions = BulkPartitioner.Partition(commands, Eager);

        partitions.Count.ShouldBe(2);
        partitions[1].Commands.Count.ShouldBe(2);
    }

    [Fact]
    public void Preserves_first_appearance_order_of_partitions()
    {
        List<IReadOnlyModificationCommand> commands =
        [
            FakeCommand.Insert("Zebra", "A"),
            FakeCommand.Insert("Apple", "A"),
            FakeCommand.Insert("Zebra", "A")
        ];

        var partitions = BulkPartitioner.Partition(commands, Eager);

        partitions.Select(p => p.TableName).ShouldBe(["Zebra", "Apple"]);
    }

    [Fact]
    public void Every_command_lands_in_exactly_one_partition()
    {
        List<IReadOnlyModificationCommand> commands =
        [
            .. Many(3, () => FakeCommand.Insert("Customers", "Name")),
            .. Many(4, () => FakeCommand.Update("Orders", "Total")),
            .. Many(2, () => FakeCommand.Delete("Lines", "Id"))
        ];

        var partitions = BulkPartitioner.Partition(commands, Eager);

        // Losing or duplicating a command would silently corrupt a save, so this is asserted
        // directly rather than inferred from the per-partition counts.
        var partitioned = partitions.SelectMany(p => p.Commands).ToList();
        partitioned.Count.ShouldBe(commands.Count);
        partitioned.ShouldBeSubsetOf(commands);
        commands.ShouldBeSubsetOf(partitioned);
    }

    [Fact]
    public void Marks_partitions_below_the_threshold()
    {
        var options = BulkOptions.Default with { Threshold = 10 };
        var commands = Many(9, () => FakeCommand.Insert("Customers", "Name"));

        var partition = BulkPartitioner.Partition(commands, options).Single();

        partition.BelowThreshold.ShouldBeTrue();
        partition.CanAccelerate.ShouldBeFalse();
        // Being small is not a shape problem, so it must not be reported as unsupported.
        partition.IneligibleReason.ShouldBeNull();
    }

    [Fact]
    public void Threshold_is_inclusive()
    {
        var options = BulkOptions.Default with { Threshold = 10 };
        var commands = Many(10, () => FakeCommand.Insert("Customers", "Name"));

        BulkPartitioner.Partition(commands, options).Single().BelowThreshold.ShouldBeFalse();
    }

    [Fact]
    public void Rejects_json_path_updates_as_a_shape_problem()
    {
        List<IReadOnlyModificationCommand> commands =
        [
            new FakeCommand
            {
                TableName = "Customers",
                EntityState = EntityState.Modified,
                ColumnModifications =
                    [new FakeColumn { ColumnName = "Data", IsWrite = true, JsonPath = "$.a" }]
            }
        ];

        var partition = BulkPartitioner.Partition(commands, Eager).Single();

        partition.IneligibleReason.ShouldNotBeNull();
        partition.IneligibleReason.ShouldContain("JSON");
        partition.CanAccelerate.ShouldBeFalse();
    }

    [Fact]
    public void Handles_an_empty_batch()
        => BulkPartitioner.Partition([], Eager).ShouldBeEmpty();

    private static FakeCommand Shaped(EntityState state, params FakeColumn[] columns)
        => new() { TableName = "Customers", EntityState = state, ColumnModifications = columns };

    private static FakeColumn Write(string name)
        => new() { ColumnName = name, IsWrite = true };

    private static FakeColumn Read(string name)
        => new() { ColumnName = name, IsRead = true };

    private static FakeColumn Condition(string name)
        => new() { ColumnName = name, IsCondition = true, IsKey = true };

    private static List<IReadOnlyModificationCommand> Many(int count, Func<FakeCommand> create)
        => [.. Enumerable.Range(0, count).Select(_ => (IReadOnlyModificationCommand)create())];
}
