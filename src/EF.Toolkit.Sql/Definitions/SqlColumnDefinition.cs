namespace EFToolkit.Sql.Definitions;

/// <summary>A column of a table-valued function's result.</summary>
public sealed class SqlColumnDefinition
{
    /// <summary>Initializes a new instance of the <see cref="SqlColumnDefinition" /> class.</summary>
    /// <param name="name">The column name. Quoted when rendered, so it may match an EF-mapped name exactly.</param>
    /// <param name="storeType">The column's database type, as the engine spells it.</param>
    public SqlColumnDefinition(string name, string storeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);

        Name = name;
        StoreType = storeType.Trim();
    }

    /// <summary>The column name.</summary>
    public string Name { get; }

    /// <summary>The column's database type.</summary>
    public string StoreType { get; }
}
