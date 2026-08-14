namespace EFToolkit.Bulk.Execution;

/// <summary>
///     A predicate restricting which target rows an operation may touch, as SQL plus its parameters.
/// </summary>
/// <remarks>
///     <para>
///         Only a synchronise consults this, and only for its delete arm. Everything else in the
///         library acts on rows the caller handed over; the delete arm is the one place a statement
///         reaches rows the caller never named, so it is the one place worth being able to fence.
///     </para>
///     <para>
///         The SQL refers to the target table as <c>t</c> and carries no literal values — every
///         value is a parameter, which is what makes the <c>FormattableString</c> overload of
///         <c>WithinScope</c> safe to hand a user-supplied value.
///     </para>
/// </remarks>
public sealed class BulkScope
{
    /// <summary>Prefix shared by every parameter a scope introduces.</summary>
    /// <remarks>
    ///     Long and library-specific so it cannot collide with a parameter the surrounding statement
    ///     already uses.
    /// </remarks>
    public const string ParameterPrefix = "__efbulk_scope_";

    /// <summary>Initializes a new scope.</summary>
    /// <param name="sql">The predicate, referring to the target table as <c>t</c>.</param>
    /// <param name="parameters">The values its parameters take, in order.</param>
    public BulkScope(string sql, IReadOnlyList<object?> parameters)
    {
        Sql = sql;
        Parameters = parameters;
    }

    /// <summary>The predicate SQL, with every value replaced by a parameter reference.</summary>
    public string Sql { get; }

    /// <summary>The parameter values, indexed as <see cref="ParameterName" /> names them.</summary>
    public IReadOnlyList<object?> Parameters { get; }

    /// <summary>The name of the parameter at <paramref name="index" />, without a marker prefix.</summary>
    /// <param name="index">Index into <see cref="Parameters" />.</param>
    /// <returns>The parameter's name.</returns>
    public static string ParameterName(int index) => ParameterPrefix + index;
}
