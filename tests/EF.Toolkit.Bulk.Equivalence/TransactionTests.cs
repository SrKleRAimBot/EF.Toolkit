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

            await context.BulkInsertAsync(
                Graph(150), o => o.IncludeGraph(), TestContext.Current.CancellationToken);

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
    ///     A synchronise removes every row its source does not contain. Those deletions are the one
    ///     thing in the library that touches rows the caller never named, so a rollback that failed
    ///     to take them back would destroy data rather than merely duplicate it.
    /// </summary>
    [Fact]
    public async Task Synchronize_restores_the_rows_its_delete_arm_removed()
    {
        await ResetAsync();

        await using (var seed = fixture.CreateBulkContext())
        {
            await seed.BulkInsertAsync(
                Customers(300), cancellationToken: TestContext.Current.CancellationToken);
        }

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            // Keeps 200, deletes 100, inserts 40.
            var desired = Customers(200);
            desired.AddRange(Customers(40, startAt: 900));

            var result = await context.BulkSynchronizeAsync(
                desired,
                o => o.MatchOn(c => c.Email).AllowFullTableDelete(),
                TestContext.Current.CancellationToken);

            result.Deleted.ShouldBe(100);
            result.Inserted.ShouldBe(40);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        // All three arms go back: the deleted hundred return and the inserted forty do not stay.
        (await CountAsync("Customers")).ShouldBe(300);
    }

    [Fact]
    public async Task Synchronize_survives_commit()
    {
        await ResetAsync();

        await using (var seed = fixture.CreateBulkContext())
        {
            await seed.BulkInsertAsync(
                Customers(300), cancellationToken: TestContext.Current.CancellationToken);
        }

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            var desired = Customers(200);
            desired.AddRange(Customers(40, startAt: 900));

            await context.BulkSynchronizeAsync(
                desired,
                o => o.MatchOn(c => c.Email).AllowFullTableDelete(),
                TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("Customers")).ShouldBe(240);
    }

    /// <summary>
    ///     The positive control for <see cref="Update_delete_merge_and_synchronize_all_roll_back" />:
    ///     without it that test would still pass if the staged paths silently wrote nothing.
    /// </summary>
    [Fact]
    public async Task Update_delete_and_merge_all_survive_commit()
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

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        // 400 updated, 100 of them deleted, 50 merged in.
        (await CountAsync("Customers")).ShouldBe(350);

        await using var check = fixture.CreateBulkContext();
        (await check.Customers.AsNoTracking()
                .CountAsync(c => c.Name == "Renamed", TestContext.Current.CancellationToken))
            .ShouldBe(300);
    }

    /// <summary>
    ///     Rows written but not yet committed must not be readable by anyone else.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every other test here proves rows are <em>gone</em> after a rollback, which a write
    ///         that never happened would also satisfy. This proves the opposite half: the rows exist
    ///         on the server and are held by the transaction rather than by nothing at all.
    ///     </para>
    ///     <para>
    ///         Under read-committed — the default on both engines — a reader facing uncommitted rows
    ///         either sees none of them or blocks on their locks. Both outcomes prove the point;
    ///         seeing them would mean the bulk copy opened a transaction of its own and committed it.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Uncommitted_bulk_rows_are_not_visible_to_another_connection()
    {
        await ResetAsync();

        await using var context = fixture.CreateBulkContext();
        await using var transaction = await context.Database
            .BeginTransactionAsync(TestContext.Current.CancellationToken);

        await context.BulkInsertAsync(
            Customers(500), cancellationToken: TestContext.Current.CancellationToken);

        // Visible to the writer, which is what makes the reader's answer meaningful.
        (await context.Customers.AsNoTracking()
                .CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(500);

        var seen = await CountFromAnotherConnectionAsync("Customers", lockWaitSeconds: 5);

        seen.ShouldBe(
            0,
            "an uncommitted bulk write must be invisible outside its transaction; a non-zero count "
            + "means the rows were committed by a transaction of the bulk copy's own");

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);

        (await CountAsync("Customers")).ShouldBe(0);
    }

    /// <summary>
    ///     The synchronous API blocks on the same asynchronous implementation, but it reaches the
    ///     providers' synchronous executor entry points, which is a separate code path to enlist on.
    /// </summary>
    [Fact]
    public async Task The_synchronous_api_is_discarded_by_a_rollback()
    {
        await ResetAsync();

        using (var context = fixture.CreateBulkContext())
        {
            using var transaction = context.Database.BeginTransaction();

            context.BulkInsert(Customers(300));

            transaction.Rollback();
        }

        (await CountAsync("Customers")).ShouldBe(0);
    }

    [Fact]
    public async Task The_synchronous_api_survives_a_commit()
    {
        await ResetAsync();

        using (var context = fixture.CreateBulkContext())
        {
            using var transaction = context.Database.BeginTransaction();

            context.BulkInsert(Customers(300));

            transaction.Commit();
        }

        (await CountAsync("Customers")).ShouldBe(300);
    }

    /// <summary>
    ///     Transparent mode under synchronous <c>SaveChanges</c>, which takes the executors'
    ///     <c>Execute</c> overload rather than <c>ExecuteAsync</c>.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(500)]
    public async Task Synchronous_save_changes_rolls_back_on_either_side_of_the_threshold(int rows)
    {
        await ResetAsync();

        using (var context = fixture.CreateBulkContext(b => b.Threshold(100)))
        {
            using var transaction = context.Database.BeginTransaction();

            context.Customers.AddRange(Customers(rows));
            context.SaveChanges();

            transaction.Rollback();
        }

        (await CountAsync("Customers")).ShouldBe(0);
    }

    /// <summary>
    ///     A transaction EF did not begin, handed to it with <c>UseTransaction</c>. EF reports it as
    ///     the current one, so a bulk write has to enlist in it exactly as it would in its own.
    /// </summary>
    [Fact]
    public async Task Bulk_operations_join_a_transaction_given_to_use_transaction()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using var ado = await connection
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.Database.UseTransactionAsync(ado, TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(300), cancellationToken: TestContext.Current.CancellationToken);

            await ado.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("Customers")).ShouldBe(0);
    }

    /// <summary>
    ///     A caller's own savepoint has to be able to undo a bulk call, which means the call's writes
    ///     — and the savepoint it takes for itself — have to be nested inside the caller's.
    /// </summary>
    [Fact]
    public async Task A_bulk_call_is_discarded_by_rolling_back_to_a_caller_savepoint()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            Assert.SkipUnless(
                transaction.SupportsSavepoints, "the engine does not support savepoints");

            await context.BulkInsertAsync(
                Customers(200), cancellationToken: TestContext.Current.CancellationToken);

            await transaction.CreateSavepointAsync(
                "caller", TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(100, startAt: 900),
                cancellationToken: TestContext.Current.CancellationToken);

            await transaction.RollbackToSavepointAsync(
                "caller", TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        // The second call is undone, the first survives.
        (await CountAsync("Customers")).ShouldBe(200);
    }

    /// <summary>
    ///     <c>WithoutSavepoint</c> gives up the ability to recover the caller's transaction after a
    ///     failure. It must not give up the transaction itself — the writes still have to be inside
    ///     it, so a rollback still takes everything.
    /// </summary>
    [Fact]
    public async Task A_failed_call_without_a_savepoint_still_commits_nothing()
    {
        await ResetAsync();

        await using (var context = fixture.CreateBulkContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(200),
                o => o.WithoutSavepoint(),
                TestContext.Current.CancellationToken);

            // Duplicate emails: the unique index rejects the whole operation.
            await Should.ThrowAsync<Exception>(
                () => context.BulkInsertAsync(
                    Customers(200),
                    o => o.WithoutSavepoint(),
                    TestContext.Current.CancellationToken));

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("Customers")).ShouldBe(0);
    }

    /// <summary>
    ///     One scope, several bulk calls and an ordinary save. Each call opens and closes the
    ///     connection around its own work, so this is where a lost enlistment would show up.
    /// </summary>
    [Fact]
    public async Task Several_operations_in_one_transaction_scope_are_discarded_together()
    {
        await ResetAsync();

        using (new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var context = fixture.CreateBulkContext();

            context.Categories.Add(new Category { Name = "Tools" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(200), cancellationToken: TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(200, startAt: 900),
                cancellationToken: TestContext.Current.CancellationToken);

            var loaded = await context.Customers.AsNoTracking()
                .ToListAsync(TestContext.Current.CancellationToken);

            foreach (var customer in loaded)
            {
                customer.Name = "Renamed";
            }

            await context.BulkUpdateAsync(
                loaded, cancellationToken: TestContext.Current.CancellationToken);

            // Disposed without Complete(), so the scope rolls back.
        }

        (await CountAsync("Customers")).ShouldBe(0);
        (await CountAsync("Categories")).ShouldBe(0);
    }

    [Fact]
    public async Task Several_operations_in_one_transaction_scope_survive_complete()
    {
        await ResetAsync();

        using (var scope = new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var context = fixture.CreateBulkContext();

            context.Categories.Add(new Category { Name = "Tools" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(200), cancellationToken: TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Customers(200, startAt: 900),
                cancellationToken: TestContext.Current.CancellationToken);

            scope.Complete();
        }

        (await CountAsync("Customers")).ShouldBe(400);
        (await CountAsync("Categories")).ShouldBe(1);
    }

    /// <summary>
    ///     A graph writes several tables in turn, so under a scope it is the operation with the most
    ///     opportunities to lose the enlistment.
    /// </summary>
    [Fact]
    public async Task A_graph_insert_inside_a_transaction_scope_is_discarded_without_complete()
    {
        await ResetAsync();

        using (new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var context = fixture.CreateBulkContext();

            await context.BulkInsertAsync(
                Graph(120), o => o.IncludeGraph(), TestContext.Current.CancellationToken);

            // Disposed without Complete(), so the scope rolls back.
        }

        (await CountAsync("Customers")).ShouldBe(0);
        (await CountAsync("Orders")).ShouldBe(0);
        (await CountAsync("OrderLines")).ShouldBe(0);
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

    /// <summary>
    ///     Counts rows from a connection that is not the one holding the open transaction.
    /// </summary>
    /// <param name="table">The table to count.</param>
    /// <param name="lockWaitSeconds">
    ///     How long to wait on another transaction's locks before giving up. SQL Server's
    ///     read-committed reader waits on the writer's locks rather than skipping the rows, so a lock
    ///     wait is the expected outcome there and proves the same thing a count of zero does — the
    ///     rows belong to a transaction that has not committed. PostgreSQL's MVCC reader answers
    ///     immediately.
    /// </param>
    /// <returns>The number of rows visible, treating a lock wait as none.</returns>
    private async Task<int> CountFromAnotherConnectionAsync(string table, int lockWaitSeconds)
    {
        await using var context = fixture.CreateBulkContext();
        var helper = context.GetService<ISqlGenerationHelper>();

        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await LockWait.LimitAsync(connection, fixture.Engine, lockWaitSeconds);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {helper.DelimitIdentifier(table)}";

        // Comfortably longer than the lock wait, so the engine's lock timeout is what ends a blocked
        // read. Reaching this one means something other than a lock is wrong, and it should say so.
        command.CommandTimeout = lockWaitSeconds * 4;

        try
        {
            var count = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            return Convert.ToInt32(count, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (DbException exception) when (LockWait.IsTimeout(exception))
        {
            // Blocked on the writer's locks until the lock timeout expired, which is only possible
            // while the rows are held by an uncommitted transaction.
            return 0;
        }
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

    /// <summary>Customers each carrying one order and one order line, for graph inserts.</summary>
    private static List<Customer> Graph(int count, int startAt = 0)
    {
        var customers = Customers(count, startAt);

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

        return customers;
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
