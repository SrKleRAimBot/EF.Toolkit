namespace EFToolkit.Sql.Security;

/// <summary>The writes a SQL Server block predicate checks.</summary>
/// <remarks>
///     A filter predicate already hides other rows from <c>UPDATE</c> and <c>DELETE</c>, so the block
///     predicates that matter for isolation are the <c>AFTER</c> ones: they stop a write from producing
///     a row the writer would not be allowed to see.
/// </remarks>
[Flags]
public enum SqlServerBlockOperations
{
    /// <summary>No block predicates.</summary>
    None = 0,

    /// <summary>Refuse inserting a row the predicate rejects.</summary>
    AfterInsert = 1,

    /// <summary>Refuse updating a row into one the predicate rejects.</summary>
    AfterUpdate = 2,

    /// <summary>Refuse updating a row the predicate rejects as it stands.</summary>
    BeforeUpdate = 4,

    /// <summary>Refuse deleting a row the predicate rejects.</summary>
    BeforeDelete = 8,
}
