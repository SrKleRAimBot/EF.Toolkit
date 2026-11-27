namespace EFToolkit.Sql.Definitions;

/// <summary>What a <see cref="FunctionDefinition" /> returns.</summary>
public enum FunctionKind
{
    /// <summary>A single value.</summary>
    Scalar,

    /// <summary>A result set, queryable like a table.</summary>
    Table,
}
