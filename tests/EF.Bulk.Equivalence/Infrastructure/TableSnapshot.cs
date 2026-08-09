using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFBulk.Equivalence.Infrastructure;

/// <summary>
///     The full contents of every mapped table, read over raw ADO.NET.
/// </summary>
/// <remarks>
///     Reading through raw SQL rather than through EF is deliberate. EF's read path applies the
///     same value converters and type mappings as its write path, so a bulk writer that converted a
///     value wrongly in a self-consistent way would round-trip cleanly through EF and the bug would
///     be invisible. Going straight to the driver compares what is actually stored.
/// </remarks>
public sealed class TableSnapshot
{
    private readonly Dictionary<string, List<object?[]>> _rows;
    private readonly Dictionary<string, string[]> _columns;

    private TableSnapshot(
        Dictionary<string, List<object?[]>> rows,
        Dictionary<string, string[]> columns)
    {
        _rows = rows;
        _columns = columns;
    }

    /// <summary>Reads every mapped table in <paramref name="context" />'s model.</summary>
    public static async Task<TableSnapshot> CaptureAsync(DbContext context)
    {
        var sqlHelper = context.GetService<ISqlGenerationHelper>();
        var rows = new Dictionary<string, List<object?[]>>(StringComparer.Ordinal);
        var columns = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync();
        }

        try
        {
            foreach (var table in DistinctTables(context.Model))
            {
                var columnNames = table.Columns.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray();
                var key = TableKey(table);

                columns[key] = columnNames;
                rows[key] = await ReadTableAsync(connection, sqlHelper, table, columnNames);
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }

        return new TableSnapshot(rows, columns);
    }

    private static IEnumerable<ITable> DistinctTables(IModel model)
        => model.GetEntityTypes()
            .SelectMany(e => e.GetTableMappings())
            .Select(m => m.Table)
            .DistinctBy(TableKey)
            .OrderBy(TableKey, StringComparer.Ordinal);

    private static string TableKey(ITable table)
        => table.Schema is null ? table.Name : $"{table.Schema}.{table.Name}";

    private static async Task<List<object?[]>> ReadTableAsync(
        DbConnection connection,
        ISqlGenerationHelper sqlHelper,
        ITable table,
        string[] columnNames)
    {
        var columnList = string.Join(", ", columnNames.Select(sqlHelper.DelimitIdentifier));
        var tableRef = sqlHelper.DelimitIdentifier(table.Name, table.Schema);

        // Ordering by the primary key makes the comparison independent of physical row order,
        // which neither engine guarantees and which bulk paths legitimately change.
        var orderBy = table.PrimaryKey is { } pk
            ? " ORDER BY " + string.Join(", ", pk.Columns.Select(c => sqlHelper.DelimitIdentifier(c.Name)))
            : "";

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {columnList} FROM {tableRef}{orderBy}";

        var result = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object?[columnNames.Length];
            for (var i = 0; i < columnNames.Length; i++)
            {
                values[i] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            }

            result.Add(values);
        }

        return result;
    }

    /// <summary>
    ///     Describes the first difference against <paramref name="other" />, or
    ///     <see langword="null" /> when the two snapshots are equivalent.
    /// </summary>
    public string? Diff(TableSnapshot other)
    {
        foreach (var (key, expected) in _rows.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!other._rows.TryGetValue(key, out var actual))
            {
                return $"Table '{key}' is missing from the EF.Bulk database.";
            }

            if (expected.Count != actual.Count)
            {
                return $"Table '{key}': stock EF wrote {expected.Count} row(s), "
                    + $"EF.Bulk wrote {actual.Count}.";
            }

            var columnNames = _columns[key];
            for (var row = 0; row < expected.Count; row++)
            {
                for (var col = 0; col < columnNames.Length; col++)
                {
                    if (!ValuesEqual(expected[row][col], actual[row][col]))
                    {
                        return $"Table '{key}', row {row}, column '{columnNames[col]}': "
                            + $"stock EF = {Format(expected[row][col])}, "
                            + $"EF.Bulk = {Format(actual[row][col])}."
                            + Environment.NewLine
                            + $"  stock row: {FormatRow(columnNames, expected[row])}"
                            + Environment.NewLine
                            + $"  bulk  row: {FormatRow(columnNames, actual[row])}";
                    }
                }
            }
        }

        return null;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return (left, right) switch
        {
            // 5.00m and 5.0m are the same stored value; scale is a formatting artefact that
            // differs between drivers.
            (decimal l, decimal r) => l == r,
            (DateTime l, DateTime r) => l.ToUniversalTime() == r.ToUniversalTime(),
            (byte[] l, byte[] r) => l.AsSpan().SequenceEqual(r),
            _ => Equals(left, right)
        };
    }

    private static string Format(object? value)
        => value switch
        {
            null => "NULL",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };

    private static string FormatRow(string[] columnNames, object?[] values)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < columnNames.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(columnNames[i]).Append('=').Append(Format(values[i]));
        }

        return sb.ToString();
    }
}
