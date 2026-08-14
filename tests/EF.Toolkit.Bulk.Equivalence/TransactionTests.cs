using System.Data.Common;
using System.Transactions;
using EFToolkit.Bulk.Equivalence.Infrastructure;
using EFToolkit.Bulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.Equivalence;

/// <summary>
///     Every bulk write must join whatever transaction is already open, so that a caller's rollback
///     discards it along with everything else.
/// </summary>
/// <remarks>
///     <para>
///         This is the property with the worst failure mode in the library. A bulk operation is
///         several statements — a staging table, a copy, a set-based statement, a drop — and a bulk
///         copy handed no transaction opens its own, committing rows the surrounding rollback was
///         supposed to discard. "Partially persisted" is far worse than "slow", and nothing about
///         it is visible from the calling code.
///     </para>
///     <para>
///         Rows are counted over raw ADO on a connection that is not the one under test, so only
///         committed state is visible and no EF-level caching can make a rolled-back write look
///         absent when it is not.
///     </para>
/// </remarks>
public abstract class TransactionTests(DatabaseFixture fixture)
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Bulk_insert_inside_a_caller_transaction_is_discarded_by_rollback()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(500), cancellationToken: TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("Customers")).ShouldBe(0);
    }

    // Without this the rollback test above would pass even if the bulk write never happened at all.
    [Fact]
    public async Task Bulk_insert_inside_a_caller_transaction_survives_commit()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(500), cancellationToken: TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("Customers")).ShouldBe(500);
    }

    /// <summary>
    ///     The mixed-mode guarantee: ordinary EF work and a bulk call in one transaction either both
    ///     land or neither does.
    /// </summary>
    [Fact]
    public async Task Save_changes_and_a_bulk_call_roll_back_together()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            context.Categories.Add(new Category { Name = "Tools" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(300), cancellationToken: TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("Customers")).ShouldBe(0);
        (await CountAsync("Categories")).ShouldBe(0);
    }

    [Fact]
    public async Task A_failure_after_a_bulk_call_discards_it()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(300), cancellationToken: TestContext.Current.CancellationToken);

            // A duplicate email violates the unique index, so this save fails after the bulk write
            // has already gone to the server.
            context.Customers.Add(new Customer
            {
                Name = "Duplicate",
                Email = "customer0@example.com",
                CreatedAt = Epoch
            });

            await Should.ThrowAsync<DbUpdateException>(
                () => context.SaveChangesAsync(TestContext.Current.CancellationToken));

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("Customers")).ShouldBe(0);
    }

    /// <summary>
    ///     An ambient <see cref="TransactionScope" /> owns the unit of work even though EF reports no
    ///     current transaction. Beginning one anyway used to throw; ignoring it would let the write
    ///     escape the scope.
    /// </summary>
    [Fact]
    public async Task Bulk_insert_inside_a_transaction_scope_is_discarded_without_complete()
    {
        await ResetAsync();

        using (new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var context = fixture.CreateBulkContext();

            await context.BulkInsertAsync(
                Customers(200), cancellationToken: TestContext.Current.CancellationToken);

            // Disposed without Complete(), so the scope rolls back.
        }

        (await CountAsync("Customers")).ShouldBe(0);
    }

    [Fact]
    public async Task Bulk_insert_inside_a_transaction_scope_survives_complete()
    {
        await ResetAsync();

        using (var scope = new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var context = fixture.CreateBulkContext();

            await context.BulkInsertAsync(
                Customers(200), cancellationToken: TestContext.Current.CancellationToken);

            scope.Complete();
        }

        (await CountAsync("Customers")).ShouldBe(200);
    }

    /// <summary>
    ///     A graph insert writes several tables in dependency order. A rollback has to take all of
    ///     them, or dependents are left pointing at principals that no longer exist.
    /// </summary>
    [Fact]
    public async Task A_graph_insert_rolls_back_completely()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            var customers = Customers(150);
            foreach (var customer in customers)
            {
                var order = new Order
                {
                    Customer = customer,
                    Reference = $"REF-{customer.Email}",
                    Status = OrderStatus.Placed,
                    PlacedAt = Epoch
                };

                order.Lines.Add(new OrderLine
                {
                    Order = order, Sku = "SKU-1", Quantity = 2, UnitPrice = 9.99m
                });

                customer.Orders.Add(order);
            }

            await context.BulkInsertAsync(
                customers, o => o.IncludeGraph(), TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("Customers")).ShouldBe(0);
        (await CountAsync("Orders")).ShouldBe(0);
        (await CountAsync("OrderLines")).ShouldBe(0);
    }

    /// <summary>
    ///     Update, delete, merge and synchronise all take the staged multi-statement path, so each
    ///     needs the same guarantee as insert.
    /// </summary>
    [Fact]
    public async Task Update_delete_merge_and_synchronize_all_roll_back()
    {
        await ResetAsync();

        await using (var seed = fixture.CreateBulkContext())
        {
            await seed.BulkInsertAsync(
                Customers(400), cancellationToken: TestContext.Current.CancellationToken);
        }

        await using (var context = fixture.CreateBulkContext())
        {
            var loaded = await context.Customers.AsNoTracking()
                .OrderBy(c => c.Id)
                .ToListAsync(TestContext.Current.CancellationToken);

            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            foreach (var customer in loaded)
            {
                customer.Name = "Renamed";
            }

            await context.BulkUpdateAsync(
                loaded, cancellationToken: TestContext.Current.CancellationToken);

            await context.BulkDeleteAsync(
                loaded.Take(100).ToList(), cancellationToken: TestContext.Current.CancellationToken);

            await context.BulkMergeAsync(
                Customers(50, startAt: 900),
                o => o.MatchOn(c => c.Email),
                TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        // Every one of those is discarded: the original 400 remain, unrenamed.
        (await CountAsync("Customers")).ShouldBe(400);

        await using var check = fixture.CreateBulkContext();
        (await check.Customers.AsNoTracking()
                .CountAsync(c => c.Name == "Renamed", TestContext.Current.CancellationToken))
            .ShouldBe(0);
    }

    /// <summary>
    ///     A partition the provider declines is replayed through stock EF Core. That replay has to
    ///     land in the same transaction and be committed with it — returning the fallback's result
    ///     straight to the caller used to skip the commit entirely, so the rows were rolled back
    ///     while the call still reported success.
    /// </summary>
    [Fact]
    public async Task A_fallback_is_committed_with_the_transaction_that_wraps_it()
    {
        await ResetAsync();

        using var recorder = new PartitionRecorder();
        await using var context = fixture.CreateBulkContext();

        var entries = Enumerable.Range(0, 200)
            .Select(i => new AuditEntry { Action = $"action-{i}" })
            .ToList();

        var result = await context.BulkInsertAsync(
            entries, cancellationToken: TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(200);

        // The reported count and the committed rows have to agree, whichever path ran.
        (await CountAsync("AuditEntries")).ShouldBe(200);

        // SQL Server correlates a generated non-key column back through MERGE ... OUTPUT, so it
        // accelerates this and there is no fallback to observe. PostgreSQL cannot, so it must fall
        // back — and if it ever stops doing so, this test has quietly stopped covering the path it
        // exists for.
        if (fixture.Engine != "sqlserver")
        {
            recorder.ExplicitFallbacks.ShouldNotBeEmpty(
                "this scenario is supposed to exercise the fall-back-to-stock-EF path");
        }
    }

    [Fact]
    public async Task A_fallback_inside_a_caller_transaction_is_discarded_by_rollback()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            var entries = Enumerable.Range(0, 200)
                .Select(i => new AuditEntry { Action = $"action-{i}" })
                .ToList();

            await context.BulkInsertAsync(
                entries, cancellationToken: TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("AuditEntries")).ShouldBe(0);
    }

    /// <summary>
    ///     Both sides of the threshold, so the accelerated path and the stock-EF path are each
    ///     covered — a fallback has to land in the same transaction as everything else.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(500)]
    public async Task Transparent_save_changes_rolls_back_on_either_side_of_the_threshold(int rows)
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext(b => b.Threshold(100)))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            context.Customers.AddRange(Customers(rows));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("Customers")).ShouldBe(0);
    }

    /// <summary>
    ///     Split across batches, each batch is its own set of statements. A rollback still has to
    ///     take all of them.
    /// </summary>
    [Fact]
    public async Task A_multi_batch_bulk_insert_rolls_back_as_one_unit()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(1000),
                o => o.BatchSize(128),
                TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("Customers")).ShouldBe(0);
    }

    /// <summary>
    ///     A failed bulk call inside a caller's transaction leaves that transaction usable, because
    ///     the operation runs behind a savepoint. Without one, PostgreSQL aborts the whole
    ///     transaction and every later statement fails until rollback.
    /// </summary>
    [Fact]
    public async Task A_failed_bulk_call_leaves_the_callers_transaction_usable()
    {
        await ResetAsync();

        await using var context = fixture.CreateBulkContext();
        await using var transaction = await context.Database
            .BeginTransactionAsync(TestContext.Current.CancellationToken);

        await context.BulkInsertAsync(
            Customers(200), cancellationToken: TestContext.Current.CancellationToken);

        // Same emails again: the unique index rejects the whole operation.
        await Should.ThrowAsync<Exception>(
            () => context.BulkInsertAsync(
                Customers(200), cancellationToken: TestContext.Current.CancellationToken));

        // The savepoint rolled back only the failed call, so the transaction still works.
        await context.BulkInsertAsync(
            Customers(50, startAt: 900), cancellationToken: TestContext.Current.CancellationToken);

        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        (await CountAsync("Customers")).ShouldBe(250);
    }

    /// <summary>
    ///     A retrying execution strategy refuses a user-initiated transaction unless the whole
    ///     operation runs inside its retry loop, so this configuration could not use the explicit
    ///     API at all.
    /// </summary>
    [Fact]
    public async Task Bulk_operations_work_under_a_retrying_execution_strategy()
    {
        await ResetAsync();

        await using var context = fixture.CreateBulkContext(retryOnFailure: true);

        await context.BulkInsertAsync(
            Customers(300), cancellationToken: TestContext.Current.CancellationToken);

        (await CountAsync("Customers")).ShouldBe(300);
    }

    [Fact]
    public async Task Bulk_operations_join_a_caller_transaction_under_a_retrying_strategy()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext(retryOnFailure: true))
        {
            var strategy = context.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database
                    .BeginTransactionAsync(TestContext.Current.CancellationToken);

                await context.BulkInsertAsync(
                    Customers(300), cancellationToken: TestContext.Current.CancellationToken);

                await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            });
        }

        (await CountAsync("Customers")).ShouldBe(0);
    }

    /// <summary>
    ///     No staging table may outlive a failed operation. They are named per operation, so a
    ///     leaked one is invisible until the schema fills up with them.
    /// </summary>
    [Fact]
    public async Task No_staging_table_survives_a_failed_operation()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            await context.BulkInsertAsync(
                Customers(200), cancellationToken: TestContext.Current.CancellationToken);

            await Should.ThrowAsync<Exception>(
                () => context.BulkInsertAsync(
                    Customers(200), cancellationToken: TestContext.Current.CancellationToken));
        }

        (await StagingTableCountAsync()).ShouldBe(0);
    }

    private async Task ResetAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();
    }

    /// <summary>Counts committed rows over raw ADO, on a connection of its own.</summary>
    private async Task<int> CountAsync(string table)
    {
        await using var context = fixture.CreateBulkContext();
        var helper = context.GetService<ISqlGenerationHelper>();

        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {helper.DelimitIdentifier(table)}";

        var count = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt32(count, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Counts leftover staging tables, whatever the engine calls them.</summary>
    private async Task<int> StagingTableCountAsync()
    {
        await using var context = fixture.CreateBulkContext();
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();

        // SQL Server stages in tempdb under a # name; PostgreSQL stages in a per-session temp
        // schema. Both are reachable from the session that would have leaked one.
        command.CommandText = fixture.Engine == "sqlserver"
            ? "SELECT COUNT(*) FROM tempdb.sys.tables WHERE name LIKE '#efbulk[_]%'"
            : "SELECT COUNT(*) FROM pg_tables WHERE tablename LIKE 'efbulk[_]%'";

        var count = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt32(count, System.Globalization.CultureInfo.InvariantCulture);
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
