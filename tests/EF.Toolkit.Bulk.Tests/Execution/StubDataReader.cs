using System.Collections;
using System.Data.Common;

namespace EFToolkit.Bulk.Tests.Execution;

/// <summary>
///     A driver whose default read type for a field is not always the type asked for.
/// </summary>
/// <remarks>
///     Standing in for a real one because that is the whole shape of the defect: the value
///     <see cref="DbDataReader.GetValue" /> returns and the value <c>GetFieldValue&lt;T&gt;</c>
///     returns are different objects, and only a driver can produce the second. This records which
///     types were asked for, so a test can say not only what came back but how it was fetched.
/// </remarks>
internal sealed class StubDataReader : DbDataReader
{
    private readonly StubField[] _fields;
    private bool _read;

    public StubDataReader(params StubField[] fields) => _fields = fields;

    /// <summary>Every type <c>GetFieldValue&lt;T&gt;</c> was called with, in order.</summary>
    public List<Type> TypedReads { get; } = [];

    public override int FieldCount => _fields.Length;
    public override int Depth => 0;
    public override bool HasRows => true;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override Type GetFieldType(int ordinal) => _fields[ordinal].FieldType;

    public override object GetValue(int ordinal)
        => _fields[ordinal].Value ?? throw new InvalidOperationException("The field is null.");

    public override T GetFieldValue<T>(int ordinal)
    {
        TypedReads.Add(typeof(T));

        if (_fields[ordinal].Typed is not { } typed)
        {
            throw new InvalidCastException(
                $"Reading as '{typeof(T)}' is not supported for '{_fields[ordinal].Name}'.");
        }

        return (T)typed(typeof(T));
    }

    public override bool IsDBNull(int ordinal) => _fields[ordinal].Value is null;

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
        => Task.FromResult(IsDBNull(ordinal));

    public override bool Read() => !_read && (_read = true);

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        => Task.FromResult(Read());

    public override bool NextResult() => false;

    public override string GetName(int ordinal) => _fields[ordinal].Name;

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _fields.Length; i++)
        {
            if (string.Equals(_fields[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "No such field.");
    }

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        for (var i = 0; i < _fields.Length && i < values.Length; i++)
        {
            values[i] = GetValue(i);
        }

        return Math.Min(values.Length, _fields.Length);
    }

    public override IEnumerator GetEnumerator() => _fields.GetEnumerator();

    // The typed accessors below are never the path under test -- everything goes through GetValue
    // or GetFieldValue<T> -- so they simply cast what the field holds.
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();
}

/// <summary>One field of a <see cref="StubDataReader" />.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="FieldType">The CLR type the driver reports for it, as <c>GetFieldType</c> would.</param>
/// <param name="Value">What a plain <c>GetValue</c> returns, or null for a database null.</param>
/// <param name="Typed">
///     Produces the value for a requested type, or null where the driver refuses to read the field
///     as anything but its default.
/// </param>
internal sealed record StubField(
    string Name,
    Type FieldType,
    object? Value,
    Func<Type, object>? Typed = null);
