using System.Data.Common;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     Builds command parameters for row values the way EF Core itself builds them.
/// </summary>
/// <remarks>
///     <para>
///         For the providers whose bulk path is ordinary parameterised statements rather than a
///         dedicated bulk protocol — MySQL and SQLite — a parameter is the whole of how a value
///         reaches the database, and what it says matters. Setting <c>Value</c> alone leaves the
///         driver to infer a type, and the driver's inference is not the provider's mapping: SQLite
///         stores a <see cref="Guid" /> as uppercase text under EF but Microsoft.Data.Sqlite binds
///         one as a blob, and a <see cref="decimal" /> as text under EF but as a real number left
///         to itself. A row written that way is a row EF cannot find again.
///     </para>
///     <para>
///         So the parameter is made by the column's own type mapping, which runs the value
///         converter and then configures the parameter exactly as it does inside
///         <c>SaveChanges</c>. That is why the value passed in is the model value, before any
///         conversion: the mapping converts it, and converting it first would do so twice.
///     </para>
/// </remarks>
internal static class BulkParameters
{
    /// <summary>Adds a parameter carrying <paramref name="value" /> for <paramref name="column" />.</summary>
    /// <param name="command">The command to add to.</param>
    /// <param name="name">The parameter name, including the provider's marker.</param>
    /// <param name="column">The column the value belongs to.</param>
    /// <param name="value">The model value, before any value converter has run.</param>
    public static void Add(DbCommand command, string name, BulkColumnInfo column, object? value)
    {
        if (column.TypeMapping is { } mapping)
        {
            command.Parameters.Add(mapping.CreateParameter(command, name, value));
            return;
        }

        AddRaw(command, name, value);
    }

    /// <summary>Adds a parameter whose type the driver infers from <paramref name="value" />.</summary>
    /// <remarks>For values with no column behind them: a staging ordinal, a scope's bound values.</remarks>
    public static void AddRaw(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Binds a scope's values, if the statement was built with one.</summary>
    /// <remarks>
    ///     Bound rather than formatted into the SQL, which is what makes the interpolated-string
    ///     overload of <c>WithinScope</c> safe to hand a value from outside the application.
    /// </remarks>
    public static void BindScope(DbCommand command, BulkScope? scope)
    {
        if (scope is null)
        {
            return;
        }

        for (var i = 0; i < scope.Parameters.Count; i++)
        {
            AddRaw(command, "@" + BulkScope.ParameterName(i), scope.Parameters[i]);
        }
    }
}
