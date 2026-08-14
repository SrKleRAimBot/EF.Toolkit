namespace EFToolkit.Audit.Configuration;

/// <summary>
///     What the keys of a payload's <c>old</c> and <c>new</c> objects are named after.
/// </summary>
public enum AuditPayloadNames
{
    /// <summary>The CLR property name. The default, and what somebody reading the trail expects.</summary>
    Property,

    /// <summary>
    ///     The database column name.
    /// </summary>
    /// <remarks>
    ///     More stable: renaming a property is a refactor that leaves years of existing entries
    ///     keyed by the old name, whereas renaming a column is a migration somebody has to think
    ///     about. Worth choosing up front if the trail will be queried by tooling rather than read.
    /// </remarks>
    Column,
}

/// <summary>
///     What an audit entry's <c>EntityType</c> column holds.
/// </summary>
public enum AuditEntityTypeNames
{
    /// <summary>The short CLR type name — <c>Order</c>. The default.</summary>
    Name,

    /// <summary>The namespace-qualified CLR type name — <c>Shop.Domain.Order</c>.</summary>
    /// <remarks>Unambiguous where two namespaces have a type of the same name.</remarks>
    FullName,

    /// <summary>The mapped table name — <c>Orders</c>.</summary>
    /// <remarks>Survives a CLR rename, and reads naturally next to a database-side query.</remarks>
    TableName,
}
