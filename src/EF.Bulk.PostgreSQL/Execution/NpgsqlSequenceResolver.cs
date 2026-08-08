using System.Collections.Concurrent;
using Npgsql;

namespace EFBulk.PostgreSQL.Execution;

/// <summary>
///     Finds the sequence backing an auto-generated integer column, so that key values can be
///     reserved up front instead of read back afterwards.
/// </summary>
/// <remarks>
///     <para>
///         EF's model records that a column is value-generated on add, but not <em>how</em> — the
///         sequence name is a database-side detail. <c>pg_get_serial_sequence</c> recovers it for
///         both <c>serial</c> columns and identity columns.
///     </para>
///     <para>
///         Why this matters more on PostgreSQL than elsewhere: <c>RETURNING</c> can only reference
///         the target table, so a staged insert has no documented way to map generated keys back to
///         the rows that produced them before PostgreSQL 17's <c>MERGE ... RETURNING</c>. Reserving
///         values makes correlation exact on every supported version.
///     </para>
/// </remarks>
public sealed class NpgsqlSequenceResolver
{
    private readonly ConcurrentDictionary<CacheKey, SequenceInfo?> _cache = new();

    private readonly record struct CacheKey(string Database, string Table, string Column);

    /// <summary>What is known about the generator behind a column.</summary>
    /// <param name="SequenceName">Fully-qualified, quoted sequence name.</param>
    /// <param name="IsGeneratedAlways">
    ///     Whether the column is <c>GENERATED ALWAYS AS IDENTITY</c>, which rejects an explicit
    ///     value unless the statement says <c>OVERRIDING SYSTEM VALUE</c> — a clause <c>COPY</c>
    ///     does not support, so such columns cannot take the reservation path.
    /// </param>
    public sealed record SequenceInfo(string SequenceName, bool IsGeneratedAlways);

    /// <summary>
    ///     Resolves the sequence behind <paramref name="column" />, or <see langword="null" /> if it
    ///     has none.
    /// </summary>
    /// <remarks>
    ///     Results are cached per database and column; the answer is a property of the schema, which
    ///     does not change while an application is running.
    /// </remarks>
    /// <param name="connection">An open connection.</param>
    /// <param name="qualifiedTable">
    ///     The table name, already delimited — <c>"Customers"</c>, not <c>Customers</c>.
    ///     <c>pg_get_serial_sequence</c> does not treat its first argument as a quoted identifier,
    ///     so an undelimited PascalCase name is folded to lower case and matches nothing.
    /// </param>
    /// <param name="column">
    ///     The bare column name. Unlike the table argument, this one <em>is</em> treated as quoted,
    ///     so its case is preserved and it must not be delimited here.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async ValueTask<SequenceInfo?> ResolveAsync(
        NpgsqlConnection connection,
        string qualifiedTable,
        string column,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var key = new CacheKey(connection.Database ?? "", qualifiedTable, column);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = await QueryAsync(connection, qualifiedTable, column, cancellationToken)
            .ConfigureAwait(false);

        _cache[key] = resolved;
        return resolved;
    }

    private static async Task<SequenceInfo?> QueryAsync(
        NpgsqlConnection connection,
        string qualified,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pg_get_serial_sequence(@table, @column),
                   (SELECT a.attidentity
                      FROM pg_attribute a
                     WHERE a.attrelid = @table::regclass
                       AND a.attname = @column
                       AND a.attnum > 0
                       AND NOT a.attisdropped)
            """;

        command.Parameters.AddWithValue("table", qualified);
        command.Parameters.AddWithValue("column", column);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var sequenceName = reader.GetString(0);
        var identity = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
            ? '\0'
            : reader.GetFieldValue<char>(1);

        return new SequenceInfo(sequenceName, identity == 'a');
    }
}
