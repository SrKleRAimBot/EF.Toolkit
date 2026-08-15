using System.Data;
using System.Data.Common;
using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using EFToolkit.Bulk.SqlServer.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace EFToolkit.Bulk.Tests.Execution;

/// <summary>
///     A bulk copy that cannot be enlisted in the transaction that is open must decline rather than
///     proceed without it.
/// </summary>
/// <remarks>
///     <para>
///         <c>SqlBulkCopy</c> takes a <see cref="SqlTransaction" /> and nothing else. When a
///         profiler or a tracing decorator has replaced EF's transaction with a wrapper of its own,
///         there is no <see cref="SqlTransaction" /> to hand over — and a bulk copy handed none opens
///         an implicit transaction and commits it, so the rows survive a rollback the caller
///         believed had discarded them.
///     </para>
///     <para>
///         Declining sends the partition through stock EF Core, which stays inside the wrapped
///         transaction because it goes through EF's own command pipeline. That is a slower write and
///         a correct one, which is the only acceptable trade here.
///     </para>
///     <para>
///         Tested at this level because the scenario cannot be built against a real database: a
///         wrapper EF cannot unwrap breaks EF's own commands too, so there is no arrangement in which
///         the surrounding save would get far enough to reach the executor.
///     </para>
/// </remarks>
public class AmbientTransactionGuardTests
{
    [Fact]
    public async Task A_transaction_that_is_not_a_sql_transaction_is_declined()
    {
        using var context = new QuotingContext();

        var executor = new SqlServerBulkExecutor(
            context.GetService<ISqlGenerationHelper>(),
            new BulkOptions());

        await using var connection = new FakeRelationalConnection(
            new SqlConnection(), new WrappedTransaction());

        var result = await executor.ExecuteAsync(
            new UnusedRowSet(), connection, TestContext.Current.CancellationToken);

        result.Handled.ShouldBeFalse(
            "a bulk copy that cannot enlist in the open transaction must decline, so the partition "
            + "is replayed through stock EF Core inside that transaction");

        (result.DeclinedReason ?? "").ShouldContain("SqlTransaction");
    }

    /// <summary>Never opened: the executor declines before it would reach the database.</summary>
    private sealed class QuotingContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlServer("Server=none;Database=none");
    }

    /// <summary>
    ///     A <see cref="DbTransaction" /> that is not a <see cref="SqlTransaction" />, standing in for
    ///     the decorators profilers install.
    /// </summary>
    private sealed class WrappedTransaction : DbTransaction, IDbContextTransaction,
        IInfrastructure<DbTransaction>
    {
        public Guid TransactionId { get; } = Guid.NewGuid();

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection? DbConnection => null;

        DbTransaction IInfrastructure<DbTransaction>.Instance => this;

        public override void Commit() => throw new NotSupportedException();

        public override void Rollback() => throw new NotSupportedException();

        public override Task CommitAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task RollbackAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => base.DisposeAsync();
    }

    /// <summary>
    ///     Only the two members the guard reads before it declines. Everything else throws, so a
    ///     future change that starts touching the database before deciding fails loudly here instead
    ///     of silently writing outside the transaction.
    /// </summary>
    private sealed class FakeRelationalConnection(
        DbConnection connection,
        IDbContextTransaction transaction) : IRelationalConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public DbConnection DbConnection
        {
            get => connection;
            set => throw new NotSupportedException();
        }

        public IDbContextTransaction? CurrentTransaction => transaction;

        public void SetDbConnection(DbConnection? value, bool contextOwnsConnection)
            => throw new NotSupportedException();

        public string? ConnectionString
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public DbContext Context => throw new NotSupportedException();

        public Guid ConnectionId => throw new NotSupportedException();

        public int? CommandTimeout
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public System.Transactions.Transaction? EnlistedTransaction
            => throw new NotSupportedException();

        public SemaphoreSlim ExclusiveLock => throw new NotSupportedException();

        public IDbContextTransaction BeginTransaction() => throw new NotSupportedException();

        public IDbContextTransaction BeginTransaction(IsolationLevel isolationLevel)
            => throw new NotSupportedException();

        public Task<IDbContextTransaction> BeginTransactionAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IDbContextTransaction> BeginTransactionAsync(
            IsolationLevel isolationLevel,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void CommitTransaction() => throw new NotSupportedException();

        public Task CommitTransactionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void RollbackTransaction() => throw new NotSupportedException();

        public Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IDbContextTransaction? UseTransaction(DbTransaction? transaction)
            => throw new NotSupportedException();

        public IDbContextTransaction? UseTransaction(DbTransaction? transaction, Guid transactionId)
            => throw new NotSupportedException();

        public Task<IDbContextTransaction?> UseTransactionAsync(
            DbTransaction? transaction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IDbContextTransaction?> UseTransactionAsync(
            DbTransaction? transaction,
            Guid transactionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void EnlistTransaction(System.Transactions.Transaction? transaction)
            => throw new NotSupportedException();

        public bool Open(bool errorsExpected = false) => throw new NotSupportedException();

        public Task<bool> OpenAsync(
            CancellationToken cancellationToken,
            bool errorsExpected = false)
            => throw new NotSupportedException();

        public bool Close() => throw new NotSupportedException();

        public Task<bool> CloseAsync() => throw new NotSupportedException();

        public void ResetState() => throw new NotSupportedException();

        public Task ResetStateAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IRelationalCommand RentCommand() => throw new NotSupportedException();

        public void ReturnCommand(IRelationalCommand command) => throw new NotSupportedException();

        public void Dispose() => connection.Dispose();

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    /// <summary>
    ///     A row set the executor must never look at, because it declines before it would need to.
    /// </summary>
    private sealed class UnusedRowSet : IBulkRowSet
    {
        public string? Schema => null;

        public string TableName => "Customers";

        public EntityState EntityState => EntityState.Added;

        public BulkOperationKind Operation => BulkOperationKind.Insert;

        public MergeCounts MergeCounts => MergeCounts.Exact;

        public TimeSpan? Timeout => null;

        public BulkScope? Scope => null;

        public int RowCount => 1;

        public IReadOnlyList<BulkColumnInfo> Columns => throw new NotSupportedException();

        public object? GetValue(int row, int column) => throw new NotSupportedException();

        public object? GetOriginalValue(int row, int column) => throw new NotSupportedException();

        public void SetGeneratedValue(int row, int column, object? value)
            => throw new NotSupportedException();

        public IReadOnlyList<IUpdateEntry> GetEntries(int row) => throw new NotSupportedException();
    }
}
