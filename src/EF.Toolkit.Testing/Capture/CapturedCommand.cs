using System.Data;

namespace EFToolkit.Testing;

/// <summary>How a captured command was executed.</summary>
public enum SqlCommandKind
{
    /// <summary><c>ExecuteReader</c> — a query returning rows.</summary>
    Reader,

    /// <summary><c>ExecuteNonQuery</c> — a write, or DDL.</summary>
    NonQuery,

    /// <summary><c>ExecuteScalar</c> — a query returning one value.</summary>
    Scalar,
}

/// <summary>One parameter of a captured command, as it was when the command ran.</summary>
/// <param name="Name">The parameter name, e.g. <c>@p0</c>.</param>
/// <param name="Value">The value. <see cref="DBNull.Value" /> is reported as <see langword="null" />.</param>
/// <param name="DbType">The parameter's <see cref="System.Data.DbType" />.</param>
/// <param name="Direction">Input, output or both.</param>
public sealed record CapturedParameter(string Name, object? Value, DbType DbType, ParameterDirection Direction);

/// <summary>One command EF sent to the database.</summary>
/// <param name="CommandText">The SQL.</param>
/// <param name="Parameters">The parameters, in command order.</param>
/// <param name="Kind">How the command was executed.</param>
/// <param name="Duration">How long it took to execute.</param>
/// <param name="Succeeded">Whether it ran without error.</param>
public sealed record CapturedCommand(
    string CommandText,
    IReadOnlyList<CapturedParameter> Parameters,
    SqlCommandKind Kind,
    TimeSpan Duration,
    bool Succeeded)
{
    /// <inheritdoc />
    public override string ToString() => CommandText;
}
