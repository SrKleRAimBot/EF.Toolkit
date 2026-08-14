using System.Runtime.CompilerServices;
using EFToolkit.Bulk.Equivalence.Infrastructure;
using EFToolkit.Bulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.Equivalence;

/// <summary>
///     Column projection, matching on something other than the primary key, and the scope that
///     fences a synchronise's delete arm.
/// </summary>
/// <remarks>
///     Run against a real engine rather than asserted on the plan, because all three change the
///     statement rather than the row set: an excluded column has to be absent from the staging
///     table's <c>SELECT</c>, a non-key match has to survive a join that matches several rows, and a
///     scope has to be a bound parameter in the delete arm of two different upsert forms.
/// </remarks>
public abstract class ProjectionAndScopeTests(DatabaseFixture fixture)
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Include_writes_only_the_named_columns()
    {
        await using var context = await SeededAsync(20);

        var customers = await context.Customers.AsNoTracking().OrderBy(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var customer in customers)
        {
            customer.Name = "renamed";
            customer.Email = $"changed{customer.Id}@example.com";
        }

        var result = await context.BulkUpdateAsync(
            customers, o => o.Include(c => c.Name), TestContext.Current.CancellationToken);

        result.Updated.ShouldBe(20);

        var stored = await Stored(context);
        stored.ShouldAllBe(c => c.Name == "renamed");

        // Email was changed on every object and named in neither the include list nor the key, so
        // an unprojected update -- which writes every non-key column -- would have taken it.
        stored.ShouldAllBe(c => c.Email.StartsWith("customer"));
    }

    [Fact]
    public async Task Exclude_leaves_the_named_columns_alone()
    {
        await using var context = await SeededAsync(15);

        var customers = await context.Customers.AsNoTracking().OrderBy(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var customer in customers)
        {
            customer.Name = "renamed";
            customer.CreatedAt = Epoch.AddYears(5);
        }

        await context.BulkUpdateAsync(
            customers, o => o.Exclude(c => c.CreatedAt), TestContext.Current.CancellationToken);

        var stored = await Stored(context);
        stored.ShouldAllBe(c => c.Name == "renamed");
        stored.ShouldAllBe(c => c.CreatedAt.Year == 2026);
    }

    [Fact]
    public async Task InsertOnly_keeps_the_existing_value_when_a_merge_updates()
    {
        await using var context = await SeededAsync(10);

        // Ten already exist and five do not, all carrying a CreatedAt five years on. The existing
        // rows should take the new name and keep their original CreatedAt; the new ones have no
        // original to keep.
        var incoming = Customers(15);
        foreach (var customer in incoming)
        {
            customer.Name = "merged";
            customer.CreatedAt = Epoch.AddYears(5);
        }

        var result = await context.BulkMergeAsync(
            incoming,
            o => o.MatchOn(c => c.Email).InsertOnly(c => c.CreatedAt),
            TestContext.Current.CancellationToken);

        result.Updated.ShouldBe(10);
        result.Inserted.ShouldBe(5);

        var stored = await Stored(context);
        stored.Count.ShouldBe(15);
        stored.ShouldAllBe(c => c.Name == "merged");

        stored.Count(c => c.CreatedAt.Year == 2026).ShouldBe(10);
        stored.Count(c => c.CreatedAt.Year == 2031).ShouldBe(5);
    }

    [Fact]
    public async Task MatchOn_updates_by_a_non_key_column()
    {
        await using var context = await SeededAsync(12);

        // Detached objects carrying no key at all: the only thing locating the row is the email.
        var incoming = Customers(12);
        foreach (var customer in incoming)
        {
            customer.Name = "matched by email";
        }

        var result = await context.BulkUpdateAsync(
            incoming, o => o.MatchOn(c => c.Email), TestContext.Current.CancellationToken);

        result.Updated.ShouldBe(12);

        var stored = await Stored(context);
        stored.ShouldAllBe(c => c.Name == "matched by email");

        // The key was never staged, so it cannot have been reassigned from the zeros above.
        stored.ShouldAllBe(c => c.Id > 0);
    }

    [Fact]
    public async Task A_non_key_match_reaches_every_row_it_matches()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(5);
        foreach (var customer in customers)
        {
            customer.Name = "duplicate";
        }

        customers.AddRange(Customers(3, startAt: 100));

        context.Customers.AddRange(customers);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await context.BulkDeleteAsync(
            [new Customer { Name = "duplicate" }],
            o => o.MatchOn(c => c.Name),
            TestContext.Current.CancellationToken);

        // One source row was given and one source row matched, so that is what is reported. Five
        // rows went; the count is what the caller passed in, not what the database touched.
        result.Deleted.ShouldBe(1);
        (await Stored(context)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_source_row_matching_nothing_is_still_a_concurrency_conflict()
    {
        await using var context = await SeededAsync(5);

        var missing = new Customer
        {
            Name = "absent",
            Email = "nobody@example.com",
            CreatedAt = Epoch
        };

        await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => context.BulkUpdateAsync(
                [missing], o => o.MatchOn(c => c.Email), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithinScope_confines_the_delete_to_the_scoped_rows()
    {
        await using var context = await SeededAsync(20);

        var ordered = await context.Customers.AsNoTracking().OrderBy(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Everything from the eleventh row on is ours to synchronise; the first ten belong to
        // someone else and must survive being absent from the source.
        var pivot = ordered[10].Id;
        var source = ordered.Skip(10).Take(5).ToList();
        foreach (var customer in source)
        {
            customer.Name = "synchronised";
        }

        var result = await context.BulkSynchronizeAsync(
            source,
            o => o.MatchOn(c => c.Email).WithinScope(c => c.Id >= pivot),
            TestContext.Current.CancellationToken);

        result.Updated.ShouldBe(5);
        result.Inserted.ShouldBe(0);
        result.Deleted.ShouldBe(5);            // rows 16-20, in scope and not in the source

        var stored = await Stored(context);
        stored.Count.ShouldBe(15);
        stored.Count(c => c.Name == "synchronised").ShouldBe(5);
        stored.Count(c => c.Id < pivot).ShouldBe(10);
    }

    [Fact]
    public async Task WithinScope_removes_the_need_to_confirm_a_full_table_delete()
    {
        await using var context = await SeededAsync(6);

        var ordered = await context.Customers.AsNoTracking().OrderBy(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        // No AllowFullTableDelete() anywhere: a scoped delete is not a full-table delete, so there
        // is nothing left to confirm.
        var result = await context.BulkSynchronizeAsync(
            ordered.Take(2).ToList(),
            o => o.MatchOn(c => c.Email).WithinScope(c => c.CreatedAt < Epoch.AddMinutes(4)),
            TestContext.Current.CancellationToken);

        result.Deleted.ShouldBe(2);            // rows 3 and 4: before the cutoff, absent from source
        (await Stored(context)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task WithinScope_accepts_raw_sql_with_bound_values()
    {
        await using var context = await SeededAsync(20);

        var ordered = await context.Customers.AsNoTracking().OrderBy(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        var pivot = ordered[10].Id;
        var column = context.GetService<ISqlGenerationHelper>()
            .DelimitIdentifier(nameof(Customer.Id));

        // Built rather than written as an interpolated literal because every hole becomes a
        // parameter -- a column name has to reach the statement as text, and only the value may be
        // a hole. Which is the point: there is no way to interpolate a value into the SQL itself.
        var scope = FormattableStringFactory.Create($"t.{column} >= {{0}}", pivot);

        var result = await context.BulkSynchronizeAsync(
            ordered.Skip(10).Take(5).ToList(),
            o => o.MatchOn(c => c.Email).WithinScope(scope),
            TestContext.Current.CancellationToken);

        result.Deleted.ShouldBe(5);

        var stored = await Stored(context);
        stored.Count.ShouldBe(15);
        stored.Count(c => c.Id < pivot).ShouldBe(10);
    }

    [Fact]
    public async Task WithinScope_is_refused_on_an_operation_that_has_no_delete_arm()
    {
        await using var context = await SeededAsync(4);

        // Silently ignoring it is the one behaviour to avoid, on the setting whose whole job is to
        // stop a delete going too wide.
        var thrown = await Should.ThrowAsync<BulkNotSupportedException>(
            () => context.BulkMergeAsync(
                Customers(2),
                o => o.MatchOn(c => c.Email).WithinScope(c => c.Id > 0),
                TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("BulkSynchronizeAsync");
    }

    [Fact]
    public async Task An_unscoped_synchronise_still_has_to_be_confirmed()
    {
        await using var context = await SeededAsync(4);

        var thrown = await Should.ThrowAsync<BulkNotSupportedException>(
            () => context.BulkSynchronizeAsync(
                Customers(2), o => o.MatchOn(c => c.Email), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("WithinScope");
        thrown.Message.ShouldContain("AllowFullTableDelete");

        // Refused before anything ran, not part-way through.
        (await Stored(context)).Count.ShouldBe(4);
    }

    private async Task<ShopContext> SeededAsync(int count)
    {
        await ResetAsync();

        var context = fixture.CreateBulkContext();
        context.Customers.AddRange(Customers(count));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        return context;
    }

    private static async Task<List<Customer>> Stored(ShopContext context)
        => await context.Customers.AsNoTracking().OrderBy(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

    private async Task ResetAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();
    }

    private static List<Customer> Customers(int count, int startAt = 0)
        =>
        [
            .. Enumerable.Range(startAt, count).Select(i => new Customer
            {
                Name = $"Customer {i}",
                Email = $"customer{i}@example.com",
                CreatedAt = Epoch.AddMinutes(i)
            })
        ];
}
