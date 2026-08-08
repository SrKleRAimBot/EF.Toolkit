using System.Data;
using EFBulk.Execution;

namespace EFBulk.SqlServer.Execution;

/// <summary>
///     Presents an <see cref="IBulkRowSet" /> to <c>SqlBulkCopy</c> as a forward-only reader.
/// </summary>
/// <remarks>
///     <c>SqlBulkCopy</c> streams from an <see cref="IDataReader" />, so rows are never materialised
///     into an intermediate table or array. Only the members <c>SqlBulkCopy</c> actually calls are
///     implemented; the rest of <see cref="IDataReader" /> would only ever be dead code.
/// </remarks>
internal sealed class BulkRowSetDataReader : IDataReader
{
    private readonly IBulkRowSet _rows;
    private readonly IReadOnlyList<int> _columnIndices;
    private readonly int _ordinalColumn;
    private int _row = -1;

    /// <param name="rows">The rows to stream.</param>
    /// <param name="columnIndices">Indices into <see cref="IBulkRowSet.Columns" />, in write order.</param>
    /// <param name="includeOrdinal">
    ///     Whether to append a synthetic trailing column carrying each row's position, used to
    ///     correlate server-generated values back through <c>MERGE ... OUTPUT</c>.
    /// </param>
    public BulkRowSetDataReader(
        IBulkRowSet rows,
        IReadOnlyList<int> columnIndices,
        bool includeOrdinal)
    {
        _rows = rows;
        _columnIndices = columnIndices;
        _ordinalColumn = includeOrdinal ? columnIndices.Count : -1;
    }

    public int FieldCount => _columnIndices.Count + (_ordinalColumn >= 0 ? 1 : 0);

    public bool Read() => ++_row < _rows.RowCount;

    public object GetValue(int i)
    {
        if (i == _ordinalColumn)
        {
            return _row;
        }

        var index = _columnIndices[i];
        var column = _rows.Columns[index];

        // A bulk copy bypasses EF's parameter construction, which is where value converters would
        // normally run, so they have to be applied explicitly here.
        return column.ToProviderValue(_rows.GetValue(_row, index)) ?? DBNull.Value;
    }

    public bool IsDBNull(int i) => GetValue(i) == DBNull.Value;

    public string GetName(int i)
        => i == _ordinalColumn
            ? SqlServerStagingInsert.OrdinalColumnName
            : _rows.Columns[_columnIndices[i]].Name;

    public Type GetFieldType(int i)
        => i == _ordinalColumn ? typeof(int) : _rows.Columns[_columnIndices[i]].ProviderClrType;

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
    public bool IsClosed => _row >= _rows.RowCount;
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
