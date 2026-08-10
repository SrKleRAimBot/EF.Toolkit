using System.Globalization;
using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EFToolkit.Bulk.PostgreSQL.Execution;

/// <summary>
///     Executes bulk row sets on PostgreSQL using binary <c>COPY</c>.
/// </summary>
/// <remarks>
///     <para>
///         Binary <c>COPY</c> is by a wide margin the fastest way to get rows into PostgreSQL: it
///         bypasses statement parsing and planning entirely and streams a compact binary
///         representation straight into the table. Its limitation is that it returns nothing.
///     </para>
///     <para>
///         Two shapes are handled. A row set with no store-generated values goes straight down the
///         copy stream. One whose only generated values are sequence-backed keys gets those keys
///         <em>reserved up front</em>, written into the stream like any other column, and propagated
///         back — one extra round trip, exact correlation, no staging table. Anything else declines.
///     </para>
/// </remarks>
public sealed class NpgsqlBulkExecutor : IBulkOperationExecutor
{
    private readonly ISqlGenerationHelper _sqlHelper;
    private readonly BulkOptions _options;
    private readonly NpgsqlSequenceResolver _sequences;

    /// <summary>Initializes a new executor.</summary>
    /// <param name="sqlHelper">Used to delimit table and column identifiers.</param>
    /// <param name="options">The context's EF.Toolkit.Bulk settings.</param>
    /// <param name="sequences">Resolves the sequences behind generated key columns.</param>
    public NpgsqlBulkExecutor(
        ISqlGenerationHelper sqlHelper,
        BulkOptions options,
        NpgsqlSequenceResolver sequences)
    {
        _sqlHelper = sqlHelper;
        _options = options;
        _sequences = sequences;
    }

    /// <inheritdoc />
    public bool CanExecute(IBulkRowSet rows, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Operation is BulkOperationKind.Merge or BulkOperationKind.Synchronize)
        {
            if (!rows.Columns.Any(c => c.IsCondition))
            {
                reason = "a merge needs match columns to detect a conflict on.";
                return false;
            }

            reason = null;
            return true;
        }

        switch (rows.EntityState)
        {
            case EntityState.Added:
                foreach (var column in rows.Columns)
                {
                    // A generated key can be reserved ahead of the insert. Anything else that must
                    // be read back -- a computed column, a default, a trigger-populated column --
                    // cannot, because COPY returns no data.
                    if (column.IsRead && !column.IsKey)
                    {
                        reason = $"'{column.Name}' is store-generated and is not a key, so its "
                            + "value can only be obtained by reading it back.";
                        return false;
                    }
                }

                reason = null;
                return true;

            case EntityState.Modified:
            case EntityState.Deleted:
                // Concurrency tokens are handled rather than declined: the loaded value is staged
                // to join on, the new value is staged separately to assign, and RETURNING carries
                // back whatever the database regenerated.
                reason = null;
                return true;

            default:
                reason = $"{rows.EntityState} has no bulk equivalent.";
                return false;
        }
    }

    /// <inheritdoc />
    public BulkExecutionResult Execute(IBulkRowSet rows, IRelationalConnection connection)
        // Npgsql's synchronous copy API is a thin wrapper over the same protocol work, and EF only
        // takes this path when the caller used the synchronous SaveChanges.
        => ExecuteAsync(rows, connection).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task<BulkExecutionResult> ExecuteAsync(
        IBulkRowSet rows,
        IRelationalConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(connection);

        var npgsqlConnection = Unwrap(connection);
        var settings = BulkExecutionSettings.Resolve(rows, _options, connection);

        if (rows.Operation is BulkOperationKind.Merge or BulkOperationKind.Synchronize)
        {
            return await MergeAsync(rows, npgsqlConnection, settings, cancellationToken)
                .ConfigureAwait(false);
        }

        if (rows.EntityState is EntityState.Modified or EntityState.Deleted)
        {
            return await ApplySetBasedAsync(rows, npgsqlConnection, settings, cancellationToken)
                .ConfigureAwait(false);
        }

        var qualifiedTable = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);

        // Column indices, not names: the copy stream is positional and this runs per value.
        var writeIndices = new List<int>();
        var generatedKeyIndices = new List<int>();

        for (var i = 0; i < rows.Columns.Count; i++)
        {
            var column = rows.Columns[i];
            if (column.IsRead && column.IsKey)
            {
                generatedKeyIndices.Add(i);
                writeIndices.Add(i);
            }
            else if (column.IsWrite)
            {
                writeIndices.Add(i);
            }
        }

        var reserved = new Dictionary<int, object?[]>();

        // Everything that could rule the row set out happens before the first row is written, so
        // declining leaves the database untouched and the caller can safely fall back.
        foreach (var index in generatedKeyIndices)
        {
            var column = rows.Columns[index];

            var sequence = await _sequences
                .ResolveAsync(npgsqlConnection, qualifiedTable, column.Name, cancellationToken)
                .ConfigureAwait(false);

            if (sequence is null)
            {
                return BulkExecutionResult.Declined(
                    $"'{column.Name}' is generated but has no sequence to reserve from.");
            }

            if (sequence.IsGeneratedAlways)
            {
                return BulkExecutionResult.Declined(
                    $"'{column.Name}' is GENERATED ALWAYS AS IDENTITY, which COPY cannot override.");
            }

            reserved[index] = await ReserveAsync(
                settings,
                    npgsqlConnection, sequence.SequenceName, column, rows.RowCount, cancellationToken)
                .ConfigureAwait(false);
        }

        var importer = await npgsqlConnection
            .BeginBinaryImportAsync(CopySql(rows, qualifiedTable, writeIndices), cancellationToken)
            .ConfigureAwait(false);

        // Resolved once per column, not once per value: how to write a column does not change
        // between rows, and doing the lookup per value dominated the copy.
        var writers = writeIndices.Select(i => NpgsqlColumnWriter.For(rows.Columns[i])).ToArray();
        var reservedKeys = writeIndices
            .Select(i => reserved.TryGetValue(i, out var keys) ? keys : null)
            .ToArray();

        ulong written;
        await using (importer.ConfigureAwait(false))
        {
            if (settings.Timeout is { } timeout)
            {
                importer.Timeout = timeout;
            }

            for (var row = 0; row < rows.RowCount; row++)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);

                for (var i = 0; i < writeIndices.Count; i++)
                {
                    // Reserved keys stream from the local array rather than from the row set, so
                    // the entities stay untouched until the copy has actually succeeded.
                    var value = reservedKeys[i] is { } keys
                        ? keys[row]
                        : rows.Columns[writeIndices[i]].ToProviderValue(
                            rows.GetValue(row, writeIndices[i]));

                    await writers[i].WriteAsync(importer, value, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            written = await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        // Only now that the rows are committed does anything learn its key. Stock EF leaves
        // temporary key values in place when a save fails, and assigning earlier would leave real
        // ones behind instead.
        foreach (var (index, keys) in reserved)
        {
            for (var row = 0; row < rows.RowCount; row++)
            {
                rows.SetGeneratedValue(row, index, keys[row]);
            }
        }

        return BulkExecutionResult.Executed(checked((int)written));
    }

    private async Task<BulkExecutionResult> MergeAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        BulkExecutionSettings settings,
        CancellationToken cancellationToken)
    {
        var writeIndices = new List<int>();
        var matchIndices = new List<int>();
        var readIndices = new List<int>();

        for (var i = 0; i < rows.Columns.Count; i++)
        {
            var column = rows.Columns[i];

            if (column.IsWrite)
            {
                writeIndices.Add(i);
            }

            if (column.IsCondition)
            {
                matchIndices.Add(i);
            }

            if (column.IsRead)
            {
                readIndices.Add(i);
            }
        }

        var (inserted, updated, deleted) = await new NpgsqlBulkMerge(
                _sqlHelper,
                settings,
                (c, t, staged, r, ct) => CopyIntoAsync(settings, c, t, staged, r, ct))
            .ExecuteAsync(
                rows, connection, writeIndices, matchIndices, readIndices,
                rows.Operation == BulkOperationKind.Synchronize, cancellationToken)
            .ConfigureAwait(false);

        return BulkExecutionResult.Merged(inserted, updated, deleted);
    }

    /// <summary>
    ///     Applies an update or delete as a single set-based statement joined to a staged copy of
    ///     the rows.
    /// </summary>
    private async Task<BulkExecutionResult> ApplySetBasedAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        BulkExecutionSettings settings,
        CancellationToken cancellationToken)
    {
        var writeIndices = new List<int>();
        var conditionIndices = new List<int>();
        var keyIndices = new List<int>();
        var readIndices = new List<int>();

        for (var i = 0; i < rows.Columns.Count; i++)
        {
            var column = rows.Columns[i];

            if (column.IsKey)
            {
                keyIndices.Add(i);
            }

            if (column.IsCondition)
            {
                conditionIndices.Add(i);
            }

            // A column can be both written and a condition -- a client-managed concurrency token
            // is the usual case -- so this is not an else.
            if (column.IsWrite)
            {
                writeIndices.Add(i);
            }

            if (column.IsRead)
            {
                readIndices.Add(i);
            }
        }

        if (keyIndices.Count == 0 || conditionIndices.Count == 0)
        {
            return BulkExecutionResult.Declined(
                $"'{rows.TableName}' has no key columns to join a staged {rows.EntityState} on.");
        }

        if (rows.EntityState == EntityState.Modified && writeIndices.Count == 0)
        {
            return BulkExecutionResult.Declined("the update has no columns to set.");
        }

        var operations = new NpgsqlBulkWriteOperations(
            _sqlHelper,
            settings,
            (c, t, staged, r, ct) => CopyIntoAsync(settings, c, t, staged, r, ct));

        var affected = rows.EntityState == EntityState.Deleted
            ? await operations
                .DeleteAsync(rows, connection, conditionIndices, keyIndices, cancellationToken)
                .ConfigureAwait(false)
            : await operations
                .UpdateAsync(
                    rows, connection, writeIndices, conditionIndices, keyIndices, readIndices,
                    cancellationToken)
                .ConfigureAwait(false);

        return BulkExecutionResult.Executed(affected);
    }

    /// <summary>Streams the given columns of every row into <paramref name="table" />.</summary>
    private async Task CopyIntoAsync(
        BulkExecutionSettings settings,
        NpgsqlConnection connection,
        string table,
        IReadOnlyList<StagingColumn> staged,
        IBulkRowSet rows,
        CancellationToken cancellationToken)
    {
        var columnList = string.Join(
            ", ",
            staged.Select(c => _sqlHelper.DelimitIdentifier(c.Name)));

        var importer = await connection
            .BeginBinaryImportAsync(
                $"COPY {table} ({columnList}) FROM STDIN (FORMAT BINARY)", cancellationToken)
            .ConfigureAwait(false);

        await using (importer.ConfigureAwait(false))
        {
            if (settings.Timeout is { } timeout)
            {
                importer.Timeout = timeout;
            }

            var writers = staged.Select(c => NpgsqlColumnWriter.For(rows.Columns[c.Index])).ToArray();

            for (var row = 0; row < rows.RowCount; row++)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);

                for (var i = 0; i < staged.Count; i++)
                {
                    await writers[i]
                        .WriteAsync(importer, staged[i].ValueFor(rows, row), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reserves <paramref name="count" /> values from <paramref name="sequenceName" />.</summary>
    /// <remarks>
    ///     Drawing every value with one <c>nextval</c> per row is concurrency-safe by construction:
    ///     each call is atomic and no other session can be handed the same value. The obvious
    ///     alternative — reading the sequence and then <c>setval</c>-ing past a block — races with
    ///     concurrent writers and can hand out values inside the block just claimed. The result is
    ///     one round trip returning <paramref name="count" /> 8-byte values, negligible next to the
    ///     row data itself.
    /// </remarks>
    private static async Task<object?[]> ReserveAsync(
        BulkExecutionSettings settings,
        NpgsqlConnection connection,
        string sequenceName,
        BulkColumnInfo keyColumn,
        int count,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        settings.Apply(command);

        // Aggregated into a single array rather than returned as one row per value. nextval is
        // still evaluated once per generated row -- same atomicity, same values -- but a hundred
        // thousand of them come back as one row instead of a hundred thousand, which is most of
        // what the reservation used to cost. nextval is parallel-unsafe, so the plan stays serial
        // and the values arrive in generation order.
        command.CommandText =
            "SELECT array_agg(nextval(@sequence)) FROM generate_series(1, @count)";
        command.Parameters.AddWithValue("sequence", sequenceName);
        command.Parameters.AddWithValue("count", count);

        var reserved = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            as long[];

        if (reserved is null || reserved.Length != count)
        {
            throw new BulkNotSupportedException(
                $"Reserved {reserved?.Length ?? 0} value(s) from sequence '{sequenceName}' but "
                + $"needed {count}.");
        }

        // nextval returns bigint regardless of the column's width, so values are narrowed before
        // they reach the copy stream. The target is the *provider* type, not the property's: a
        // strongly-typed key converts int-to-struct, and Convert.ChangeType cannot produce a struct
        // — it is the value converter's job, which runs later when the value is written back onto
        // the entity.
        var keyType = keyColumn.ProviderClrType;

        var values = new object?[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = keyType == typeof(long)
                ? reserved[i]
                : Convert.ChangeType(reserved[i], keyType, CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static NpgsqlConnection Unwrap(IRelationalConnection connection)
        => connection.DbConnection as NpgsqlConnection
            ?? throw new BulkNotSupportedException(
                "EF.Toolkit.Bulk.PostgreSQL requires an NpgsqlConnection, but the context is using "
                + $"'{connection.DbConnection.GetType().Name}'.");

    private string CopySql(IBulkRowSet rows, string qualifiedTable, List<int> writeIndices)
    {
        var columnList = string.Join(
            ", ",
            writeIndices.Select(i => _sqlHelper.DelimitIdentifier(rows.Columns[i].Name)));

        return $"COPY {qualifiedTable} ({columnList}) FROM STDIN (FORMAT BINARY)";
    }

}
