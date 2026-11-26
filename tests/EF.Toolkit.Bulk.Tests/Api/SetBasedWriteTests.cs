using System.Data.Common;
using System.Linq.Expressions;
using EFToolkit.Bulk.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFToolkit.Bulk.Tests.Api;

/// <summary>
///     <c>UpdateWhere</c> and <c>DeleteWhere</c>, run against a real (in-memory SQLite) database so
///     that what is asserted is the rows that changed, not the shape of an expression tree.
/// </summary>
public sealed class SetBasedWriteTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly List<string> _commands = [];

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        context.Tickets.AddRange(
            new Ticket { Id = 1, Title = "a", Status = TicketStatus.Open, Points = 1, Tenant = 1, Location = new() { City = "Oslo", Street = "s1" } },
            new Ticket { Id = 2, Title = "b", Status = TicketStatus.Open, Points = 2, Tenant = 1, Location = new() { City = "Oslo", Street = "s2" } },
            new Ticket { Id = 3, Title = "c", Status = TicketStatus.Closed, Points = 3, Tenant = 1, Location = new() { City = "Rome", Street = "s3" } },
            new Ticket { Id = 4, Title = "d", Status = TicketStatus.Open, Points = 4, Tenant = 2, Location = new() { City = "Rome", Street = "s4" } });
        context.Labels.Add(new Label { Id = 1, Name = "bug" });
        await context.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    // ---------------------------------------------------------------------------------------
    // UpdateWhere — object initializer
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Updates_only_the_rows_matching_the_condition()
    {
        await using var context = CreateContext();

        var updated = await context.Tickets.UpdateWhereAsync(
            t => t.Status == TicketStatus.Open && t.Points < 3,
            t => new Ticket { Status = TicketStatus.Closed },
            TestContext.Current.CancellationToken);

        updated.ShouldBe(2);
        (await Statuses()).ShouldBe(
            [TicketStatus.Closed, TicketStatus.Closed, TicketStatus.Closed, TicketStatus.Open]);
    }

    [Fact]
    public async Task Sets_several_columns_and_leaves_the_rest_alone()
    {
        await using var context = CreateContext();

        await context.Tickets.UpdateWhereAsync(
            t => t.Id == 1,
            t => new Ticket { Title = "renamed", Points = 10 },
            TestContext.Current.CancellationToken);

        var row = await Fresh(1);
        row.Title.ShouldBe("renamed");
        row.Points.ShouldBe(10);
        row.Status.ShouldBe(TicketStatus.Open);
        row.Location.City.ShouldBe("Oslo");
    }

    [Fact]
    public async Task A_value_can_refer_to_the_row_it_replaces()
    {
        // The reason the initializer is a lambda over the row: 'Points = t.Points * 2' has to become
        // SET Points = Points * 2, evaluated per row by the database.
        await using var context = CreateContext();

        await context.Tickets.UpdateWhereAsync(
            t => t.Tenant == 1,
            t => new Ticket { Points = t.Points * 2, Title = t.Title + "!" },
            TestContext.Current.CancellationToken);

        (await Points()).ShouldBe([2, 4, 6, 4]);
        (await Fresh(3)).Title.ShouldBe("c!");
    }

    [Fact]
    public async Task Captured_variables_are_sent_as_parameters_not_inlined()
    {
        await using var context = CreateContext();
        var title = "x'); DROP TABLE Tickets; --";
        var threshold = 2;

        await context.Tickets.UpdateWhereAsync(
            t => t.Points >= threshold,
            t => new Ticket { Title = title },
            TestContext.Current.CancellationToken);

        (await Fresh(4)).Title.ShouldBe(title);
        _commands.Last(c => c.StartsWith("UPDATE", StringComparison.Ordinal))
            .ShouldNotContain("DROP TABLE");
    }

    [Fact]
    public async Task A_nested_initializer_sets_columns_of_a_complex_property()
    {
        await using var context = CreateContext();

        await context.Tickets.UpdateWhereAsync(
            t => t.Location.City == "Rome",
            t => new Ticket { Location = { City = "Milan" } },
            TestContext.Current.CancellationToken);

        (await Fresh(3)).Location.City.ShouldBe("Milan");
        (await Fresh(3)).Location.Street.ShouldBe("s3");   // the sibling column is untouched
        (await Fresh(1)).Location.City.ShouldBe("Oslo");
    }

    [Fact]
    public async Task A_condition_matching_nothing_updates_nothing()
    {
        await using var context = CreateContext();

        var updated = await context.Tickets.UpdateWhereAsync(
            t => t.Points > 100,
            t => new Ticket { Points = 0 },
            TestContext.Current.CancellationToken);

        updated.ShouldBe(0);
        (await Points()).ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Composes_with_a_query_that_is_already_filtered()
    {
        await using var context = CreateContext();

        var updated = await context.Tickets
            .Where(t => t.Tenant == 2)
            .UpdateWhereAsync(
                t => t.Status == TicketStatus.Open,
                t => new Ticket { Points = 0 },
                TestContext.Current.CancellationToken);

        updated.ShouldBe(1);
        (await Points()).ShouldBe([1, 2, 3, 0]);
    }

    [Fact]
    public async Task Respects_global_query_filters_unless_they_are_ignored()
    {
        await using var context = CreateContext(tenant: 1);

        (await context.Tickets.UpdateWhereAsync(
            t => true, t => new Ticket { Points = 0 }, TestContext.Current.CancellationToken)).ShouldBe(3);
        (await Points()).ShouldBe([0, 0, 0, 4]);

        (await context.Tickets.IgnoreQueryFilters().UpdateWhereAsync(
            t => true, t => new Ticket { Points = 9 }, TestContext.Current.CancellationToken)).ShouldBe(4);
        (await Points()).ShouldBe([9, 9, 9, 9]);
    }

    [Fact]
    public async Task The_context_overload_reaches_the_same_rows()
    {
        await using var context = CreateContext();

        var updated = await context.UpdateWhereAsync<Ticket>(
            t => t.Id == 2,
            t => new Ticket { Status = TicketStatus.Closed },
            TestContext.Current.CancellationToken);

        updated.ShouldBe(1);
        (await Fresh(2)).Status.ShouldBe(TicketStatus.Closed);
    }

    [Fact]
    public async Task The_synchronous_overloads_behave_the_same()
    {
        await using var context = CreateContext();

        context.Tickets.UpdateWhere(t => t.Id == 1, t => new Ticket { Points = 50 }).ShouldBe(1);
        context.UpdateWhere<Ticket>(t => t.Id == 2, t => new Ticket { Points = 60 }).ShouldBe(1);

        (await Points()).ShouldBe([50, 60, 3, 4]);
    }

    [Fact]
    public async Task Does_not_touch_entities_the_context_is_tracking()
    {
        // Same contract as ExecuteUpdate: the statement runs against the database, and a tracked
        // instance keeps the values it was loaded with until it is reloaded.
        await using var context = CreateContext();
        var tracked = await context.Tickets.SingleAsync(t => t.Id == 1, TestContext.Current.CancellationToken);

        await context.Tickets.UpdateWhereAsync(
            t => t.Id == 1, t => new Ticket { Points = 99 }, TestContext.Current.CancellationToken);

        tracked.Points.ShouldBe(1);
        context.Entry(tracked).State.ShouldBe(EntityState.Unchanged);
        (await Fresh(1)).Points.ShouldBe(99);
    }

    [Fact]
    public async Task Runs_inside_an_ambient_transaction()
    {
        await using var context = CreateContext();
        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await context.Tickets.UpdateWhereAsync(
                t => true, t => new Ticket { Points = 0 }, TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await Points()).ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        await using var context = CreateContext();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => context.Tickets.UpdateWhereAsync(
            t => true, t => new Ticket { Points = 0 }, cancelled.Token));
    }

    // ---------------------------------------------------------------------------------------
    // UpdateWhere — setter builder
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_setter_builder_overload_serves_entities_that_cannot_be_initialised()
    {
        // Label has a required member, so 'new Label { … }' would not compile without setting it.
        await using var context = CreateContext();

        (await context.Labels.UpdateWhereAsync(
            l => l.Id == 1,
            s => s.SetProperty(l => l.Name, l => l.Name + "-fix"),
            TestContext.Current.CancellationToken)).ShouldBe(1);

        context.UpdateWhere<Label>(l => l.Id == 1, s => s.SetProperty(l => l.Colour, "red")).ShouldBe(1);
        context.Labels.UpdateWhere(l => l.Id == 1, s => s.SetProperty(l => l.Colour, l => l.Colour + "!")).ShouldBe(1);
        (await context.UpdateWhereAsync<Label>(
            l => l.Id == 1, s => s.SetProperty(l => l.Priority, 5), TestContext.Current.CancellationToken)).ShouldBe(1);

        await using var fresh = CreateContext();
        var label = await fresh.Labels.SingleAsync(TestContext.Current.CancellationToken);
        label.Name.ShouldBe("bug-fix");
        label.Colour.ShouldBe("red!");
        label.Priority.ShouldBe(5);
    }

    // ---------------------------------------------------------------------------------------
    // DeleteWhere
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Deletes_only_the_rows_matching_the_condition()
    {
        await using var context = CreateContext();

        (await context.Tickets.DeleteWhereAsync(
            t => t.Status == TicketStatus.Open && t.Tenant == 1,
            TestContext.Current.CancellationToken)).ShouldBe(2);

        (await Ids()).ShouldBe([3, 4]);
    }

    [Fact]
    public async Task Delete_composes_and_respects_query_filters()
    {
        await using var context = CreateContext(tenant: 2);

        (await context.Tickets.DeleteWhereAsync(t => true, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await Ids()).ShouldBe([1, 2, 3]);

        (await context.Tickets.IgnoreQueryFilters().Where(t => t.Tenant == 1)
            .DeleteWhereAsync(t => t.Points > 1, TestContext.Current.CancellationToken)).ShouldBe(2);
        (await Ids()).ShouldBe([1]);
    }

    [Fact]
    public async Task Delete_overloads_on_the_context_and_synchronous()
    {
        await using var context = CreateContext();

        (await context.DeleteWhereAsync<Ticket>(t => t.Id == 1, TestContext.Current.CancellationToken)).ShouldBe(1);
        context.DeleteWhere<Ticket>(t => t.Id == 2).ShouldBe(1);
        context.Tickets.DeleteWhere(t => t.Id == 3).ShouldBe(1);
        context.Tickets.DeleteWhere(t => t.Id == 3).ShouldBe(0);

        (await Ids()).ShouldBe([4]);
    }

    // ---------------------------------------------------------------------------------------
    // Argument validation
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Null_arguments_are_refused()
    {
        await using var context = CreateContext();
        IQueryable<Ticket> nullSource = null!;

        Should.Throw<ArgumentNullException>(() => nullSource.UpdateWhere(t => true, t => new Ticket { Points = 1 }));
        Should.Throw<ArgumentNullException>(() => context.Tickets.UpdateWhere(null!, t => new Ticket { Points = 1 }));
        Should.Throw<ArgumentNullException>(() => context.Tickets.UpdateWhere(t => true, (Expression<Func<Ticket, Ticket>>)null!));
        Should.Throw<ArgumentNullException>(() => context.Tickets.UpdateWhere(t => true, (Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Ticket>>)null!));
        Should.Throw<ArgumentNullException>(() => context.Tickets.DeleteWhere(null!));
        Should.Throw<ArgumentNullException>(() => ((DbContext)null!).DeleteWhere<Ticket>(t => true));
    }

    [Fact]
    public async Task A_rejected_initializer_runs_no_statement()
    {
        await using var context = CreateContext();
        _commands.Clear();

        await Should.ThrowAsync<ArgumentException>(() => context.Tickets.UpdateWhereAsync(
            t => true, t => new Ticket(), TestContext.Current.CancellationToken));

        _commands.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------------

    private SetBasedContext CreateContext(int? tenant = null)
        => new(new DbContextOptionsBuilder<SetBasedContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new CommandRecorder(_commands))
            .Options, tenant);

    private async Task<Ticket> Fresh(int id)
    {
        await using var context = CreateContext();
        return await context.Tickets.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private async Task<List<int>> Points()
    {
        await using var context = CreateContext();
        return await context.Tickets.OrderBy(t => t.Id).Select(t => t.Points).ToListAsync();
    }

    private async Task<List<TicketStatus>> Statuses()
    {
        await using var context = CreateContext();
        return await context.Tickets.OrderBy(t => t.Id).Select(t => t.Status).ToListAsync();
    }

    private async Task<List<int>> Ids()
    {
        await using var context = CreateContext();
        return await context.Tickets.OrderBy(t => t.Id).Select(t => t.Id).ToListAsync();
    }

    private sealed class CommandRecorder(List<string> commands) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            commands.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }
    }
}

public enum TicketStatus
{
    Open,
    Closed,
}

public sealed class Ticket
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public TicketStatus Status { get; set; }
    public int Points { get; set; }
    public int Tenant { get; set; }
    public TicketLocation Location { get; set; } = new();
}

public sealed class TicketLocation
{
    public string City { get; set; } = "";
    public string Street { get; set; } = "";
}

public sealed class Label
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Colour { get; set; } = "";
    public int Priority { get; set; }
}

public sealed class SetBasedContext(DbContextOptions<SetBasedContext> options, int? tenant) : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Label> Labels => Set<Label>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ticket>(b =>
        {
            b.Property(t => t.Id).ValueGeneratedNever();
            b.ComplexProperty(t => t.Location);
            b.HasQueryFilter(t => tenant == null || t.Tenant == tenant);
        });
        modelBuilder.Entity<Label>().Property(l => l.Id).ValueGeneratedNever();
    }
}
