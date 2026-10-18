using System.Data.Common;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.MySql.Execution;

/// <summary>
///     The connection, transaction and settings one MySQL operation runs under, plus the multi-row
///     <c>INSERT</c> writer and the staging-table helpers every statement builder shares.
/// </summary>
/// <remarks>
///     <para>
///         MySQL's own bulk-load path, <c>LOAD DATA LOCAL INFILE</c>, is off by default on both the
///         server and every driver, for good security reasons, and an application cannot be
///         expected to switch it on to use a library. A multi-row <c>INSERT</c> needs nothing
///         switched on, and it is where nearly all of the gain is anyway: one statement parsed,
///         planned and acknowledged per thousand rows rather than per row.
///     </para>
///     <para>
///         Statements are bounded three ways — by rows, by parameters, and by an estimate of their
///         size on the wire — so that a set of wide or text-heavy rows stays well inside the
///         server's <c>max_allowed_packet</c> rather than failing on it partway through.
///     </para>
/// </remarks>
internal sealed class MySqlSession : BulkCommandSession
{
    /// <summary>The most rows one <c>INSERT</c> statement carries.</summary>
    internal const int MaxRowsPerStatement = 1_000;

    /// <summary>The most parameters one statement binds.</summary>
    /// <remarks>Comfortably below the protocol's limit of 65,535 for a prepared statement.</remarks>
    internal const int MaxParametersPerStatement = 30_000;

    /// <summary>The estimated size at which a statement is cut short.</summary>
    /// <remarks>
    ///     A sixteenth of MySQL 8's default <c>max_allowed_packet</c>, and a quarter of MySQL 5.7's,
    ///     leaving room for escaping and the estimate being only that.
    /// </remarks>
    internal const long MaxStatementBytes = 4L * 1024 * 1024;

    public MySqlSession(
        ISqlGenerationHelper sqlHelper,
        DbConnection connection,
        DbTransaction? transaction,
        BulkExecutionSettings settings)
        : base(sqlHelper, connection, transaction, settings)
    {
    }

    /// <summary>
    ///     Writes <paramref name="layout" /> for every row in <paramref name="rowIndices" /> into
    ///     <paramref name="table" />, many rows to a round trip.
    /// </summary>
    /// <param name="table">The destination, delimited.</param>
    /// <param name="columnList">The destination columns, delimited, in layout order.</param>
    /// <param name="rows">The rows whose values are written.</param>
    /// <param name="layout">Which value each destination column takes.</param>
    /// <param name="includeOrdinal">
    ///     Whether to append each row's position as a final value, for a staging table's ordinal.
    /// </param>
    /// <param name="rowIndices">The rows to write, in order.</param>
    /// <param name="keys">
    ///     How to recover the AUTO_INCREMENT key each row is given, or <see langword="null" /> when
    ///     nothing generated needs correlating.
    /// </param>
    /// <param name="generated">Receives each row's key, when <paramref name="keys" /> is given.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task InsertRowsAsync(
        string table,
        string columnList,
        IBulkRowSet rows,
        IReadOnlyList<StagingColumn> layout,
        bool includeOrdinal,
        IReadOnlyList<int> rowIndices,
        GeneratedKeys? keys,
        BulkGeneratedValues? generated,
        CancellationToken cancellationToken)
    {
        var fields = layout.Count + (includeOrdinal ? 1 : 0);
        var maxRows = Math.Max(1, Math.Min(MaxRowsPerStatement, MaxParametersPerStatement / Math.Max(1, fields)));

        // One INSERT listing every row, unless keys must come back and the server cannot promise a
        // statement's keys are consecutive. Then each row is its own INSERT, followed by the
        // LAST_INSERT_ID() that names its key exactly — still a whole chunk to a round trip.
        var perRow = keys is { Consecutive: false };
        var prefix = $"INSERT INTO {table} ({columnList}) VALUES ";

        var start = 0;
        while (start < rowIndices.Count)
        {
            await using var command = CreateCommand("");
            var sql = new System.Text.StringBuilder(prefix);

            var bytes = (long)sql.Length;
            var count = 0;

            // At least one row per round trip, however large: a single row that exceeds the
            // estimate still has to be written, and the server is the judge of whether it fits.
            while (start + count < rowIndices.Count
                && count < maxRows
                && (count == 0 || bytes < MaxStatementBytes))
            {
                var row = rowIndices[start + count];

                if (count > 0)
                {
                    sql.Append(perRow ? prefix : ", ");
                }

                sql.Append('(');

                for (var i = 0; i < layout.Count; i++)
                {
                    var name = Parameter(command.Parameters.Count);
                    var value = layout[i].ModelValueFor(rows, row);

                    sql.Append(i == 0 ? "" : ", ").Append(name);
                    BulkParameters.Add(command, name, rows.Columns[layout[i].Index], value);

                    bytes += Estimate(value);
                }

                if (includeOrdinal)
                {
                    var name = Parameter(command.Parameters.Count);
                    sql.Append(layout.Count == 0 ? "" : ", ").Append(name);
                    BulkParameters.AddRaw(command, name, row);
                    bytes += 16;
                }

                sql.Append(')');

                if (perRow)
                {
                    sql.Append("; SELECT LAST_INSERT_ID(); ");
                    bytes += prefix.Length + 30;
                }

                count++;
            }

            command.CommandText = sql.ToString();

            if (keys is null)
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (perRow)
            {
                await ReadEachKeyAsync(command, keys, generated!, rowIndices, start, count, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                // Per connection, so no other session's insert can move it; and for a multi-row
                // statement it is the value generated for the statement's first row.
                await using var last = CreateCommand("SELECT LAST_INSERT_ID()");
                var first = Convert.ToInt64(
                    await last.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);

                for (var k = 0; k < count; k++)
                {
                    keys.Assign(generated!, rowIndices[start + k], first + (k * keys.Increment));
                }
            }

            start += count;
        }
    }

    /// <summary>Reads the key each of a chunk's single-row inserts reported, in order.</summary>
    private static async Task ReadEachKeyAsync(
        System.Data.Common.DbCommand command,
        GeneratedKeys keys,
        BulkGeneratedValues generated,
        IReadOnlyList<int> rowIndices,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        var k = 0;

        do
        {
            // The INSERTs produce no result set and the SELECTs one row each, so whatever has
            // columns is the next row's key — however a driver presents the statements between.
            if (reader.FieldCount == 0)
            {
                continue;
            }

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (k >= count)
                {
                    throw new BulkNotSupportedException(
                        $"A chunk of {count} insert(s) reported more keys than rows. This indicates "
                        + "a bug in EF.Toolkit.Bulk.MySql's key handling.");
                }

                keys.Assign(
                    generated,
                    rowIndices[start + k],
                    Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));

                k++;
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        if (k != count)
        {
            throw new BulkNotSupportedException(
                $"A chunk of {count} insert(s) reported {k} key(s). This indicates a bug in "
                + "EF.Toolkit.Bulk.MySql's key handling.");
        }
    }

    /// <summary>
    ///     Creates a temporary staging table shaped like <paramref name="staged" />, with each
    ///     column's type taken from the target column it carries.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>CREATE TEMPORARY TABLE ... SELECT ... LIMIT 0</c> copies the source columns' types
    ///         without copying rows, which avoids translating EF's store types into DDL and getting a
    ///         facet wrong. It does not copy <c>AUTO_INCREMENT</c>, generated-column expressions or
    ///         constraints, so the staged values are exactly the ones written.
    ///     </para>
    ///     <para>
    ///         Any index is declared here rather than added after the load, which is the opposite of
    ///         what the other engines do and is forced: creating or dropping a <em>temporary</em>
    ///         table does not end the transaction, but every other DDL statement on one does, and an
    ///         implicit commit in the middle of a bulk operation is the one thing it must never do.
    ///     </para>
    /// </remarks>
    /// <param name="rows">The rows being staged.</param>
    /// <param name="staging">The staging table's name, delimited.</param>
    /// <param name="staged">The staging columns.</param>
    /// <param name="includeOrdinal">Whether to add the ordinal column.</param>
    /// <param name="index">Staging columns to index, or empty for none.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    public Task CreateStagingAsync(
        IBulkRowSet rows,
        string staging,
        IReadOnlyList<StagingColumn> staged,
        bool includeOrdinal,
        IReadOnlyList<string> index,
        CancellationToken cancellationToken)
    {
        var projection = string.Join(
            ", ",
            staged.Select(c => $"{Quote(rows.Columns[c.Index].Name)} AS {Quote(c.Name)}"));

        if (includeOrdinal)
        {
            projection += $"{(staged.Count == 0 ? "" : ", ")}CAST(0 AS SIGNED) AS {Quote(StagingColumn.OrdinalColumnName)}";
        }

        var definition = index.Count == 0
            ? ""
            : $"(INDEX ({string.Join(", ", index.Select(Quote))})) ";

        return ExecuteNonQueryAsync(
            $"CREATE TEMPORARY TABLE {staging} {definition}SELECT {projection} FROM {Target(rows)} LIMIT 0",
            null,
            cancellationToken);
    }

    /// <summary>Drops a staging table without letting the attempt mask an exception in flight.</summary>
    public Task DropStagingAsync(string staging)
        => StagingCleanup.RunAsync(
            staging,
            // TEMPORARY, so that even a name collision could never drop a real table — and so the
            // statement does not commit the transaction, which a plain DROP TABLE would.
            () => ExecuteNonQueryAsync(
                $"DROP TEMPORARY TABLE IF EXISTS {staging}", null, CancellationToken.None));

    /// <summary>
    ///     Which of <paramref name="indices" />' staged names to index, or none at all when the set
    ///     is too small to be worth it or a column cannot be indexed without a prefix length.
    /// </summary>
    public static IReadOnlyList<string> IndexFor(
        IBulkRowSet rows,
        IReadOnlyList<int> indices,
        Func<int, string> stagedName,
        int threshold)
    {
        if (!StagingPrelude.ShouldIndex(rows.RowCount, indices.Count, threshold))
        {
            return [];
        }

        foreach (var index in indices)
        {
            var storeType = rows.Columns[index].TypeMapping?.StoreType ?? "";

            // MySQL indexes TEXT, BLOB and JSON only over a declared prefix, and a prefix index
            // cannot answer an equality join on its own. Better no index than an error.
            if (storeType.Contains("text", StringComparison.OrdinalIgnoreCase)
                || storeType.Contains("blob", StringComparison.OrdinalIgnoreCase)
                || storeType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }
        }

        return [.. indices.Select(stagedName)];
    }

    /// <summary>A unique staging table name, delimited.</summary>
    public string NewStagingName() => Quote($"efbulk_{Guid.NewGuid():N}");

    /// <summary>The name of the parameter at <paramref name="index" />.</summary>
    public static string Parameter(int index) => "@p" + index;

    /// <summary>A rough upper bound on what a value adds to the statement on the wire.</summary>
    private static long Estimate(object? value) => value switch
    {
        // Escaped text can double, and UTF-8 takes up to four bytes a character.
        string text => 16 + (text.Length * 4L),
        byte[] bytes => 16 + (bytes.Length * 2L),
        _ => 32,
    };
}
