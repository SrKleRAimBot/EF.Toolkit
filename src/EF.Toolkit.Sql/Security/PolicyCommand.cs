namespace EFToolkit.Sql.Security;

/// <summary>The commands a PostgreSQL row-level security policy applies to.</summary>
public enum PolicyCommand
{
    /// <summary>Every command. Takes <c>USING</c>, <c>WITH CHECK</c> or both.</summary>
    All,

    /// <summary><c>SELECT</c>. Takes <c>USING</c> only.</summary>
    Select,

    /// <summary><c>INSERT</c>. Takes <c>WITH CHECK</c> only.</summary>
    Insert,

    /// <summary><c>UPDATE</c>. Takes <c>USING</c>, <c>WITH CHECK</c> or both.</summary>
    Update,

    /// <summary><c>DELETE</c>. Takes <c>USING</c> only.</summary>
    Delete,
}
