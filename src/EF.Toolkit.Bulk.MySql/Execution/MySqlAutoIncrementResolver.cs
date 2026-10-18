using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;

namespace EFToolkit.Bulk.MySql.Execution;

/// <summary>
///     Establishes whether a store-generated key column really is <c>AUTO_INCREMENT</c>, so that
///     <c>LAST_INSERT_ID()</c> can be relied on to describe it.
/// </summary>
/// <remarks>
///     EF's model records that a column is generated on add, but not how. A key filled by a trigger
///     or a <c>DEFAULT (UUID_TO_BIN(UUID()))</c> expression is just as "generated" to EF, and
///     <c>LAST_INSERT_ID()</c> knows nothing about either — it would report a stale value from some
///     earlier statement, and every entity would be handed a key that is not its own. So the schema
///     is asked, once per column for the life of the process.
/// </remarks>
public sealed class MySqlAutoIncrementResolver
{
    private readonly ConcurrentDictionary<CacheKey, bool> _cache = new();

    private readonly record struct CacheKey(string Server, string Database, string Table, string Column);

    /// <summary>Whether <paramref name="column" /> of <paramref name="table" /> is <c>AUTO_INCREMENT</c>.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction the connection is in, if any.</param>
    /// <param name="schema">The table's database, or <see langword="null" /> for the connection's.</param>
    /// <param name="table">The bare table name.</param>
    /// <param name="column">The bare column name.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async ValueTask<bool> IsAutoIncrementAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string? schema,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var key = new CacheKey(connection.DataSource, schema ?? connection.Database, table, column);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXTRA FROM information_schema.COLUMNS "
            + "WHERE TABLE_SCHEMA = COALESCE(@schema, DATABASE()) "
            + "AND TABLE_NAME = @table AND COLUMN_NAME = @column";

        Add(command, "@schema", schema);
        Add(command, "@table", table);
        Add(command, "@column", column);

        var extra = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        var resolved = extra?.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) == true;

        _cache[key] = resolved;
        return resolved;
    }

    private static void Add(DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = (object?)value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
