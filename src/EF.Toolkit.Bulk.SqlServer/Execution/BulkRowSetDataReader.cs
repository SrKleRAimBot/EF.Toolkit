using System.Data;
using EFToolkit.Bulk.Execution;

namespace EFToolkit.Bulk.SqlServer.Execution;

/// <summary>
///     Presents an <see cref="IBulkRowSet" /> to <c>SqlBulkCopy</c> as a forward-only reader.
/// </summary>
/// <remarks>
///     <c>SqlBulkCopy</c> streams from an <see cref="IDataReader" />, so rows are never materialised
///     into an intermediate table or array. Only the members <c>SqlBulkCopy</c> actually calls are
///     implemented; the rest of <see cref="IDataReader" /> would only ever be dead code.
///     <para>
///         Values come from a <see cref="BulkRowCursor" />, which fills the whole row once. That
///         matters because <c>SqlBulkCopy</c> asks <c>IsDBNull</c> and then <c>GetValue</c> for
///         every cell: answering the first by fetching the value, as this used to, read each cell
///         and ran each value converter twice.
///     </para>
/// </remarks>
internal sealed class BulkRowSetDataReader : IDataReader
{
    private readonly IBulkRowSet _rows;
    private readonly IReadOnlyList<StagingColumn> _columns;
    private readonly BulkRowCursor _cursor;
    private readonly int _ordinalColumn;

    /// <param name="rows">The rows to stream.</param>
    /// <param name="columns">The staging layout, in write order.</param>
    /// <param name="includeOrdinal">
    ///     Whether to append a synthetic trailing column carrying each row's position, used to
    ///     correlate server-generated values back through <c>MERGE ... OUTPUT</c>.
    /// </param>
    public BulkRowSetDataReader(
        IBulkRowSet rows,
        IReadOnlyList<StagingColumn> columns,
        bool includeOrdinal)
    {
        _rows = rows;
        _columns = columns;
        _cursor = new BulkRowCursor(rows, columns, includeOrdinal);
        _ordinalColumn = includeOrdinal ? columns.Count : -1;
    }

    public int FieldCount => _cursor.FieldCount;

    public bool Read() => _cursor.MoveNext();

    public object GetValue(int i) => _cursor[i] ?? DBNull.Value;

    public bool IsDBNull(int i) => _cursor.IsNull(i);

    public string GetName(int i)
        => i == _ordinalColumn
            ? SqlServerStagingInsert.OrdinalColumnName
            : _columns[i].Name;

    public Type GetFieldType(int i)
        => i == _ordinalColumn ? typeof(int) : _rows.Columns[_columns[i].Index].ProviderClrType;

    public int GetOrdinal(string name)
    {
        for (var i = 0; i < FieldCount; i++)
        {
            if (string.Equals(GetName(i), name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(name), name, "The bulk copy source has no such column.");
    }

    public int Depth => 0;
    public bool IsClosed => _cursor.IsExhausted;
    public int RecordsAffected => -1;

    public void Close() { }
    public void Dispose() { }
    public bool NextResult() => false;

    public object this[int i] => GetValue(i);
    public object this[string name] => GetValue(GetOrdinal(name));

    public bool GetBoolean(int i) => (bool)GetValue(i);
    public byte GetByte(int i) => (byte)GetValue(i);
    public char GetChar(int i) => (char)GetValue(i);
    public DateTime GetDateTime(int i) => (DateTime)GetValue(i);
    public decimal GetDecimal(int i) => (decimal)GetValue(i);
    public double GetDouble(int i) => (double)GetValue(i);
    public float GetFloat(int i) => (float)GetValue(i);
    public Guid GetGuid(int i) => (Guid)GetValue(i);
    public short GetInt16(int i) => (short)GetValue(i);
    public int GetInt32(int i) => (int)GetValue(i);
    public long GetInt64(int i) => (long)GetValue(i);
    public string GetString(int i) => (string)GetValue(i);

    public int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public string GetDataTypeName(int i) => GetFieldType(i).Name;

    public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();

    public long GetChars(int i, long fieldOffset, char[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();

    public IDataReader GetData(int i) => throw new NotSupportedException();

    public DataTable? GetSchemaTable() => null;
}
