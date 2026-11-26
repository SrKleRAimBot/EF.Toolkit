using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Security;

/// <summary>A parameter of a SQL Server security predicate function, and the column bound to it.</summary>
public sealed class SqlServerPredicateParameter
{
    /// <summary>Initializes a new instance of the <see cref="SqlServerPredicateParameter" /> class.</summary>
    /// <param name="name">The parameter name, without <c>@</c>. The predicate refers to it as <c>@name</c>.</param>
    /// <param name="storeType">The parameter's type, which should match the column's.</param>
    /// <param name="column">The table column passed to the parameter for each row.</param>
    public SqlServerPredicateParameter(string name, string storeType, string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        Name = SqlSyntax.RequireSimpleIdentifier(name.StartsWith('@') ? name[1..] : name, nameof(name));
        StoreType = storeType.Trim();
        Column = column;
    }

    /// <summary>The parameter name, without <c>@</c>.</summary>
    public string Name { get; }

    /// <summary>The parameter's type.</summary>
    public string StoreType { get; }

    /// <summary>The column bound to the parameter.</summary>
    public string Column { get; }
}
