using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Audit.Capture;

/// <summary>
///     Captures audit entries for changes saved through <c>SaveChanges()</c>.
/// </summary>
/// <remarks>
///     <para>
///         Capture is split across two interception points because neither one alone has everything
///         an entry needs. Before the save, the values are all there but a store-generated key is
///         still a placeholder. After it, the keys are real but the original values have been
///         overwritten and deleted entries detached. So the change set is snapshotted in
///         <c>SavingChanges</c> and the generated values are re-read in <c>SavedChanges</c>.
///     </para>
///     <para>
///         Which leaves the transaction. Where none exists and the configured atomicity asks for
///         one, this opens it before the save — EF then uses it and does not commit a transaction it
///         did not create — and commits it after the entries are written. That is what makes an
///         entry naming a store-generated key atomic with the row that generated it.
///     </para>
///     <para>
///         Holding state between the two calls is safe because EF builds one internal
///         service-provider scope per <c>DbContext</c> instance, so this instance belongs to exactly
///         one context, and a context does not perform two saves at once. The sink's own save is
///         re-entrant into these very methods, which is what <c>_writing</c> guards.
///     </para>
///     <para>
///         This also covers EF.Toolkit.Bulk's transparent mode for free: that replaces a service
///         below <c>SaveChanges</c>, so a save accelerated by it still passes through here
///         unchanged. Only the explicit bulk API, which bypasses the change tracker entirely, needs
///         a capture path of its own.
///     </para>
/// </remarks>
internal sealed class AuditSaveChangesInterceptor(
    AuditOptions options,
    IAuditEntryFactory factory,
    IAuditSink sink) : ISaveChangesInterceptor
{
    private List<ChangeTrackerCaptureSource>? _pending;
    private IDbContextTransaction? _owned;
    private bool _writing;

    /// <inheritdoc />
    public InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (Capture(eventData) is { } context)
        {
            _owned = AuditTransaction.Begin(context, options);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Capture(eventData) is { } context)
        {
            _owned = await AuditTransaction.BeginAsync(context, options, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Blocks on the asynchronous path rather than duplicating it. A sink is asynchronous by
    ///     contract, and the alternative is a second implementation of the part most likely to be
    ///     subtly different from the first.
    /// </remarks>
    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        => SavedChangesAsync(eventData, result, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (_writing)
        {
            // The sink's own save completing. The outer save still owns the pending state and the
            // transaction, and must be the one to finish them.
            return result;
        }

        if (_pending is not { Count: > 0 } sources || eventData.Context is not { } context)
        {
            Reset();
            return result;
        }

        _pending = null;
        _writing = true;

        try
        {
            foreach (var source in sources)
            {
                source.RefreshGeneratedValues();
            }

            var entries = await factory
                .CreateAsync([.. sources], cancellationToken)
                .ConfigureAwait(false);

            if (entries.Count > 0)
            {
                await WriteAsync(context, entries, cancellationToken).ConfigureAwait(false);
            }

            if (_owned is { } transaction)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await RollbackAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _writing = false;
            await DisposeTransactionAsync().ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public void SaveChangesFailed(DbContextErrorEventData eventData)
        => SaveChangesFailedAsync(eventData, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _ = eventData;
        _ = cancellationToken;

        if (_writing)
        {
            // The sink's own save failed. SavedChangesAsync is already unwinding and will roll the
            // change back with it, which is the whole point of OnAuditFailure(Throw).
            return;
        }

        _pending = null;
        await RollbackAsync().ConfigureAwait(false);
        await DisposeTransactionAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void SaveChangesCanceled(DbContextEventData eventData)
        => SaveChangesCanceledAsync(eventData, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _ = eventData;
        _ = cancellationToken;

        if (_writing)
        {
            return;
        }

        _pending = null;
        await RollbackAsync().ConfigureAwait(false);
        await DisposeTransactionAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Snapshots the pending change set, and says whether a transaction is now needed.
    /// </summary>
    /// <returns>
    ///     The context when there is something to audit, otherwise <see langword="null" /> — so that
    ///     a save touching nothing audited never opens a transaction it does not need.
    /// </returns>
    private DbContext? Capture(DbContextEventData eventData)
    {
        if (_writing || eventData.Context is not { } context)
        {
            // The sink's own save re-enters here. Its entries are not audited, so this would find
            // nothing anyway — but it would also overwrite the state the outer save is holding.
            return null;
        }

        _pending = AuditChangeSet.Build(context, options);

        return _pending.Count > 0 ? context : null;
    }

    private async Task WriteAsync(
        DbContext context,
        IReadOnlyList<AuditEntry> entries,
        CancellationToken cancellationToken)
    {
        var writeContext = new AuditWriteContext(
            context, _owned ?? context.Database.CurrentTransaction, AuditSources.SaveChanges);

        try
        {
            await sink.WriteAsync(entries, writeContext, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AuditDiagnostics.ReportSinkFailed(AuditSources.SaveChanges, entries.Count, exception);

            if (options.OnAuditFailure == AuditFailure.Throw)
            {
                throw;
            }
        }
    }

    private async Task RollbackAsync()
    {
        if (_owned is not { } transaction)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch
        {
            // The transaction may already be gone — a failed statement on PostgreSQL aborts it, and
            // a connection that dropped takes it with it. Throwing here would replace the real
            // failure with a bookkeeping one.
        }
    }

    private async Task DisposeTransactionAsync()
    {
        if (_owned is { } transaction)
        {
            _owned = null;
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Reset()
    {
        _pending = null;
        _owned?.Dispose();
        _owned = null;
    }
}
