using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Definitions;

/// <summary>A parameter of a stored procedure or function.</summary>
/// <remarks>
///     The name is written without a prefix; SQL Server's <c>@</c> is added when rendering for it (a
///     leading <c>@</c> is accepted and ignored). It must be a plain identifier because the routine
///     body refers to it bare, so it cannot be quoted the way table and column names are.
/// </remarks>
public sealed class SqlParameterDefinition
{
    /// <summary>Initializes a new instance of the <see cref="SqlParameterDefinition" /> class.</summary>
    /// <param name="name">The parameter name, without a prefix.</param>
    /// <param name="storeType">The parameter's database type, as the engine spells it (<c>integer</c>, <c>nvarchar(100)</c>).</param>
    /// <param name="direction">The parameter's direction. Functions accept only <see cref="SqlParameterDirection.In" />.</param>
    public SqlParameterDefinition(
        string name,
        string storeType,
        SqlParameterDirection direction = SqlParameterDirection.In)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Not a defined direction.");
        }

        Name = SqlSyntax.RequireSimpleIdentifier(name.StartsWith('@') ? name[1..] : name, nameof(name));
        StoreType = storeType.Trim();
        Direction = direction;
    }

    /// <summary>The parameter name, without a prefix.</summary>
    public string Name { get; }

    /// <summary>The parameter's database type.</summary>
    public string StoreType { get; }

    /// <summary>The parameter's direction.</summary>
    public SqlParameterDirection Direction { get; }
}
