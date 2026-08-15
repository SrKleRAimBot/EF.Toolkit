using System.Data.Common;
using System.Transactions;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Equivalence.Infrastructure;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Equivalence;

/// <summary>
///     An audit entry belongs to the same unit of work as the change it describes, whoever owns that
///     unit of work.
/// </summary>
/// <remarks>
///     <para>
///         Two failure modes, both silent. A committed change with no entry leaves a gap nobody
///         notices until an auditor asks. An entry for a change that was rolled back is worse: the
///         trail asserts something that never happened, and the row it names does not exist to
///         contradict it. Neither shows up in ordinary use, which is why every write path gets its
///         own scenario here rather than an argument that it must be fine.
///     </para>
///     <para>
///         Both directions are checked throughout. A rollback test alone would pass just as happily
///         against auditing that never wrote anything at all, so each one is paired with the commit
///         that proves there was something to discard.
///     </para>
///     <para>
///         Entries are read over raw ADO on a connection of the harness's own, so only committed
///         state is visible and nothing EF is holding can make a rolled-back entry look absent when
///         it is not.
///     </para>
/// </remarks>
public abstract class AuditTransactionTests(AuditDatabaseFixture fixture)
{
    // ---------------------------------------------------------------- SaveChanges

    /// <summary>
    ///     The positive control for every rollback scenario below: the same change, committed,
    ///     produces the entry that the rollbacks are proving is absent.
    /// </summary>
    [Fact]
    public async Task A_committed_change_keeps_its_entry()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            context.Products.Add(NewProduct());
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(1);
        (await CountProductsAsync()).ShouldBe(1);
    }

    /// <summary>
    ///     A save succeeds and is audited, then something later in the same transaction fails. The
    ///     entry has to go back with the change it described.
    /// </summary>
    [Fact]
    public async Task A_later_failure_in_the_callers_transaction_discards_the_entry()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            context.Products.Add(NewProduct("SKU-first"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            // A duplicate SKU violates the unique index, so this save fails after the first has
            // already been written and audited.
            context.Products.Add(NewProduct("SKU-first"));

            await Should.ThrowAsync<DbUpdateException>(
                () => context.SaveChangesAsync(TestContext.Current.CancellationToken));

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountProductsAsync()).ShouldBe(0);
    }

    /// <summary>
    ///     The synchronous interceptor path, which opens and commits the transaction through the
    ///     blocking overloads rather than the asynchronous ones.
    /// </summary>
    [Fact]
    public async Task Synchronous_save_changes_rolls_back_with_its_entries()
    {
        await ResetAsync();

        using (var context = fixture.CreateContext())
        {
            using var transaction = context.Database.BeginTransaction();

            context.Products.Add(NewProduct());
            context.SaveChanges();

            transaction.Rollback();
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountProductsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Synchronous_save_changes_keeps_its_entries_on_commit()
    {
        await ResetAsync();

        using (var context = fixture.CreateContext())
        {
            using var transaction = context.Database.BeginTransaction();

            context.Products.Add(NewProduct());
            context.SaveChanges();

            transaction.Commit();
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(1);
        (await CountProductsAsync()).ShouldBe(1);
    }

    /// <summary>
    ///     Rolling back to a caller's savepoint has to take the entry too, which it can only do if
    ///     the entry was written after the savepoint and inside the same transaction.
    /// </summary>
    [Fact]
    public async Task Rolling_back_to_a_caller_savepoint_discards_the_change_and_its_entry()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            Assert.SkipUnless(
                transaction.SupportsSavepoints, "the engine does not support savepoints");

            context.Products.Add(NewProduct("SKU-kept"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await transaction.CreateSavepointAsync(
                "caller", TestContext.Current.CancellationToken);

            context.Products.Add(NewProduct("SKU-undone"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await transaction.RollbackToSavepointAsync(
                "caller", TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);

        entries.Count.ShouldBe(1);
        (await CountProductsAsync()).ShouldBe(1);
    }

    // ---------------------------------------------------------------- ambient scopes

    /// <summary>
    ///     An ambient <see cref="TransactionScope" /> owns the unit of work even though EF reports no
    ///     current transaction. Auditing must not open one of its own — that would throw — and must
    ///     not treat the absence as licence to commit separately.
    /// </summary>
    [Fact]
    public async Task Entries_are_discarded_with_a_transaction_scope()
    {
        await ResetAsync();

        using (new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var context = fixture.CreateContext();

            context.Products.Add(NewProduct());
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Disposed without Complete(), so the scope rolls back.
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountProductsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Entries_survive_a_completed_transaction_scope()
    {
        await ResetAsync();

        using (var scope = new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var context = fixture.CreateContext();

            context.Products.Add(NewProduct());
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            scope.Complete();
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(1);
        (await CountProductsAsync()).ShouldBe(1);
    }

    // ---------------------------------------------------------------- isolation

    /// <summary>
    ///     An entry written but not yet committed must not be readable by anyone else.
    /// </summary>
    /// <remarks>
    ///     Every rollback test proves an entry is absent afterwards, which auditing that never wrote
    ///     anything would satisfy too. This proves the other half: the entry exists on the server and
    ///     is held by the transaction. Under read-committed a reader either sees none of it or blocks
    ///     on its locks — both mean it is uncommitted, and both are treated as invisible here.
    /// </remarks>
    [Fact]
    public async Task An_uncommitted_entry_is_not_visible_to_another_connection()
    {
        await ResetAsync();

        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database
            .BeginTransactionAsync(TestContext.Current.CancellationToken);

        context.Products.Add(NewProduct());
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await CountEntriesFromAnotherConnectionAsync(lockWaitSeconds: 5)).ShouldBe(
            0,
            "an uncommitted audit entry must be invisible outside its transaction; a non-zero count "
            + "means auditing committed it separately from the change it describes");

        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        // And visible the moment it is committed, so the reader above was capable of seeing it.
        (await CountEntriesFromAnotherConnectionAsync(lockWaitSeconds: 5)).ShouldBe(1);
    }

    // ---------------------------------------------------------------- audit-owned transactions

    /// <summary>
    ///     With no caller transaction, auditing opens its own so the row and its entry land together.
    /// </summary>
    [Fact]
    public async Task Auditing_commits_the_row_and_the_entry_together_when_it_owns_the_transaction()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            context.Products.Add(NewProduct());
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Nothing is left open: the transaction auditing began is committed and disposed by the
            // time the save returns.
            context.Database.CurrentTransaction.ShouldBeNull();
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(1);
        (await CountProductsAsync()).ShouldBe(1);
    }

    /// <summary>
    ///     Best-effort atomicity gives up the shared transaction by name, so a change may outlive a
    ///     sink that could not record it. That is a legitimate choice; it is not the default, and it
    ///     has to be asked for.
    /// </summary>
    [Fact]
    public async Task Best_effort_atomicity_does_not_open_a_transaction()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext(
            a => a.Atomicity(AuditAtomicity.BestEffort)))
        {
            context.Products.Add(NewProduct());
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            context.Database.CurrentTransaction.ShouldBeNull();
        }

        // Still written — best-effort weakens the guarantee, it does not disable auditing.
        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(1);
        (await CountProductsAsync()).ShouldBe(1);
    }

    // ---------------------------------------------------------------- execution strategies

    /// <summary>
    ///     A retrying execution strategy refuses a user-initiated transaction unless the whole unit
    ///     of work runs inside its retry loop, which an interceptor cannot arrange. Auditing says so
    ///     rather than opening a second transaction that commits on its own while the configuration
    ///     claims the entry and the change are atomic.
    /// </summary>
    [Fact]
    public async Task Same_transaction_atomicity_refuses_a_retrying_execution_strategy()
    {
        await ResetAsync();

        await using var context = fixture.CreateContext(retryOnFailure: true);

        context.Products.Add(NewProduct());

        var thrown = await Should.ThrowAsync<AuditNotSupportedException>(
            () => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("retrying execution strategy");

        // Refused before anything was written, so the store is untouched.
        (await CountProductsAsync()).ShouldBe(0);
        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
    }

    /// <summary>
    ///     The remedy the refusal names: the caller opens the transaction inside the strategy's own
    ///     retry loop, and auditing joins it rather than needing one of its own.
    /// </summary>
    [Fact]
    public async Task A_retrying_strategy_works_when_the_caller_owns_the_transaction()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext(retryOnFailure: true))
        {
            var strategy = context.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database
                    .BeginTransactionAsync(TestContext.Current.CancellationToken);

                context.Products.Add(NewProduct());
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);

                await transaction.CommitAsync(TestContext.Current.CancellationToken);
            });
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(1);
        (await CountProductsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_retrying_strategy_rolls_the_entry_back_with_the_caller_transaction()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext(retryOnFailure: true))
        {
            var strategy = context.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database
                    .BeginTransactionAsync(TestContext.Current.CancellationToken);

                context.Products.Add(NewProduct());
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);

                await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            });
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountProductsAsync()).ShouldBe(0);
    }

    /// <summary>
    ///     Best-effort atomicity never needed a transaction of its own, so a retrying strategy is no
    ///     obstacle to it.
    /// </summary>
    [Fact]
    public async Task Best_effort_atomicity_accepts_a_retrying_execution_strategy()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext(
            a => a.Atomicity(AuditAtomicity.BestEffort), retryOnFailure: true))
        {
            context.Products.Add(NewProduct());
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(1);
        (await CountProductsAsync()).ShouldBe(1);
    }

    // ---------------------------------------------------------------- transparent bulk mode

    /// <summary>
    ///     Transparent mode replaces a service below <c>SaveChanges</c>, so the save is accelerated
    ///     but still audited by the interceptor. Both the rows and the entries have to stay inside
    ///     the caller's transaction.
    /// </summary>
    /// <remarks>
    ///     Run on both sides of the acceleration threshold, because the accelerated path and the
    ///     stock-EF path reach the database through entirely different code.
    /// </remarks>
    [Theory]
    [InlineData(5)]
    [InlineData(200)]
    public async Task Transparent_bulk_save_changes_rolls_back_with_its_entries(int rows)
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            context.Products.AddRange(Products(rows));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountProductsAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(200)]
    public async Task Transparent_bulk_save_changes_keeps_its_entries_on_commit(int rows)
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            context.Products.AddRange(Products(rows));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(rows);
        (await CountProductsAsync()).ShouldBe(rows);
    }

    // ---------------------------------------------------------------- explicit bulk API

    /// <summary>
    ///     The explicit API bypasses the change tracker, so its entries come from the observer seam
    ///     instead. Update, delete and merge additionally take a before-image read, which is another
    ///     statement that has to be inside the same transaction.
    /// </summary>
    [Fact]
    public async Task Bulk_update_entries_roll_back_with_the_rows()
    {
        await ResetAsync();

        var products = Products(20);
        await SeedAsync(products);
        await ClearAuditAsync();

        foreach (var product in products)
        {
            product.Name = "Renamed";
        }

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkUpdateAsync(
                products, cancellationToken: TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountRenamedAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Bulk_update_entries_survive_commit()
    {
        await ResetAsync();

        var products = Products(20);
        await SeedAsync(products);
        await ClearAuditAsync();

        foreach (var product in products)
        {
            product.Name = "Renamed";
        }

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkUpdateAsync(
                products, cancellationToken: TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(20);
        (await CountRenamedAsync()).ShouldBe(20);
    }

    [Fact]
    public async Task Bulk_delete_entries_roll_back_with_the_rows()
    {
        await ResetAsync();

        var products = Products(20);
        await SeedAsync(products);
        await ClearAuditAsync();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkDeleteAsync(
                products, cancellationToken: TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        // The rows come back, and no entry claims they ever went away.
        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountProductsAsync()).ShouldBe(20);
    }

    [Fact]
    public async Task Bulk_merge_entries_roll_back_with_the_rows()
    {
        await ResetAsync();

        var existing = Products(10);
        await SeedAsync(existing);
        await ClearAuditAsync();

        // Ten updates and ten inserts, so both arms of the merge produce entries.
        var source = Products(20);
        foreach (var product in source)
        {
            product.Name = "Merged";
        }

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkMergeAsync(
                source,
                o => o.MatchOn(p => p.Sku),
                TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountProductsAsync()).ShouldBe(10);
    }

    [Fact]
    public async Task Bulk_entries_are_discarded_with_a_transaction_scope()
    {
        await ResetAsync();

        using (new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var context = fixture.CreateContext(bulk: true);

            await context.BulkInsertAsync(
                Products(20), cancellationToken: TestContext.Current.CancellationToken);

            // Disposed without Complete(), so the scope rolls back.
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountProductsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Bulk_entries_survive_a_completed_transaction_scope()
    {
        await ResetAsync();

        using (var scope = new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var context = fixture.CreateContext(bulk: true);

            await context.BulkInsertAsync(
                Products(20), cancellationToken: TestContext.Current.CancellationToken);

            scope.Complete();
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(20);
        (await CountProductsAsync()).ShouldBe(20);
    }

    /// <summary>
    ///     Above the batch threshold the entries are written through EF.Toolkit.Bulk rather than
    ///     <c>AddRange</c> — a different write path, inside the same transaction, and one that must
    ///     not take a savepoint of its own that would let a failed audit write survive.
    /// </summary>
    [Fact]
    public async Task The_batched_entry_write_stays_inside_the_transaction()
    {
        await ResetAsync();

        // Comfortably above AuditOptions.DefaultBatchThreshold, so the batch writer is what runs.
        const int rows = 250;

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Products(rows), cancellationToken: TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
        (await CountProductsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task The_batched_entry_write_survives_commit()
    {
        await ResetAsync();

        const int rows = 250;

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Products(rows), cancellationToken: TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(rows);
        (await CountProductsAsync()).ShouldBe(rows);
    }

    /// <summary>
    ///     A bulk call that fails is undone by its own savepoint, which has to take the entries it
    ///     had already written with it — otherwise the trail records a write that was rolled back
    ///     while the caller's transaction goes on to commit successfully.
    /// </summary>
    [Fact]
    public async Task A_failed_bulk_call_leaves_behind_neither_rows_nor_entries()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Products(20), cancellationToken: TestContext.Current.CancellationToken);

            // The same SKUs again: the unique index rejects the whole operation.
            await Should.ThrowAsync<Exception>(
                () => context.BulkInsertAsync(
                    Products(20), cancellationToken: TestContext.Current.CancellationToken));

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        // The savepoint undid the failed call and nothing else: the first twenty survive, once.
        (await CountProductsAsync()).ShouldBe(20);
        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(20);
    }

    /// <summary>
    ///     An explicit bulk call with no caller transaction opens its own, and the entries the
    ///     observer produces are committed with it rather than after it.
    /// </summary>
    [Fact]
    public async Task A_bulk_call_that_owns_its_transaction_commits_rows_and_entries_together()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkInsertAsync(
                Products(20), cancellationToken: TestContext.Current.CancellationToken);

            context.Database.CurrentTransaction.ShouldBeNull();
        }

        (await AuditSnapshot.ReadAsync(fixture)).Count.ShouldBe(20);
        (await CountProductsAsync()).ShouldBe(20);
    }

    // ---------------------------------------------------------------- helpers

    private Task ResetAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        return fixture.ResetAsync();
    }

    private async Task<int> CountProductsAsync()
    {
        await using var context = fixture.CreateContext(auditing: false);
        return await context.Products.CountAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountRenamedAsync()
    {
        await using var context = fixture.CreateContext(auditing: false);
        return await context.Products
            .CountAsync(p => p.Name == "Renamed", TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     Counts audit entries from a connection that is not the one holding the open transaction.
    /// </summary>
    /// <param name="lockWaitSeconds">
    ///     How long to wait on another transaction's locks before giving up. SQL Server's
    ///     read-committed reader waits on the writer's locks rather than skipping the rows, so a lock
    ///     wait is the expected outcome there and proves the same thing a count of zero does.
    ///     PostgreSQL's MVCC reader answers immediately.
    /// </param>
    /// <returns>The number of entries visible, treating a lock wait as none.</returns>
    private async Task<int> CountEntriesFromAnotherConnectionAsync(int lockWaitSeconds)
    {
        await using var connection = fixture.OpenConnection();

        await LockWait.LimitAsync(connection, fixture.Engine, lockWaitSeconds);

        await using var command = connection.CreateCommand();

        command.CommandText =
            $"SELECT COUNT(*) FROM {fixture.Quote(AuditOptions.DefaultSchema)}."
            + $"{fixture.Quote(AuditOptions.DefaultTableName)}";

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

    /// <summary>Removes the entries a seeding step produced, so a scenario starts clean.</summary>
    private async Task ClearAuditAsync()
    {
        await using var connection = fixture.OpenConnection();
        await using var command = connection.CreateCommand();

        command.CommandText =
            $"DELETE FROM {fixture.Quote(AuditOptions.DefaultSchema)}."
            + $"{fixture.Quote(AuditOptions.DefaultTableName)}";

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes rows without auditing them, so a scenario can start from existing data.</summary>
    private async Task SeedAsync(IEnumerable<Product> products)
    {
        await using var context = fixture.CreateContext(auditing: false);
        context.Products.AddRange(products);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Product NewProduct(string sku = "SKU-1") => new()
    {
        Sku = sku,
        Name = "Widget",
        Price = 9.99m,
        Status = ProductStatus.Draft,
        TenantId = "acme",
    };

    private static List<Product> Products(int count, int startAt = 1)
        => [.. Enumerable.Range(startAt, count).Select(i => NewProduct($"SKU-{i}"))];
}
