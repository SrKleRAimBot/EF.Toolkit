using Microsoft.EntityFrameworkCore.Update;

namespace EFToolkit.Bulk.Planning;

/// <summary>
///     Compares modification commands by the shape a bulk operation cares about: same table, same
///     state, same written columns in the same order, same read columns, same condition columns,
///     same rows-affected column.
/// </summary>
/// <remarks>
///     <para>
///         This exists so that grouping a batch allocates nothing. The obvious implementation
///         builds a string key per command — a <see cref="System.Text.StringBuilder" />, its char
///         buffer, and the resulting string, three allocations for every row — on the code path
///         whose entire purpose is to stop allocating per row. Hashing and equality are the only
///         things the key was ever used for, and both can be computed from the command itself, so
///         the first command of each group serves as its own key.
///     </para>
///     <para>
///         <strong>Roles are compared in three separate passes, not one interleaved walk.</strong>
///         A single walk comparing <c>(name, role)</c> pairs in column order is a strictly stronger
///         relation than comparing the write list, then the read list, then the condition list: it
///         would additionally require the roles to interleave identically. Commands that differ
///         that way are hard to construct, but "hard to construct" is not "cannot happen", and the
///         grouping this feeds decides which rows share a bulk statement.
///     </para>
///     <para>
///         Commands are mutable, so using one as a dictionary key is only safe because partitioning
///         completes before anything writes to them. <see cref="GetHashCode" /> reads only the
///         table, state and column names, none of which a bulk execution changes — values and
///         generated results do change, and are deliberately not part of the hash.
///     </para>
/// </remarks>
internal sealed class ModificationCommandShapeComparer
    : IEqualityComparer<IReadOnlyModificationCommand>
{
    /// <summary>The shared instance.</summary>
    public static ModificationCommandShapeComparer Instance { get; } = new();

    private ModificationCommandShapeComparer()
    {
    }

    /// <inheritdoc />
    public bool Equals(IReadOnlyModificationCommand? x, IReadOnlyModificationCommand? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return x.EntityState == y.EntityState
            && string.Equals(x.TableName, y.TableName, StringComparison.Ordinal)
            && string.Equals(x.Schema, y.Schema, StringComparison.Ordinal)
            && string.Equals(
                x.RowsAffectedColumn?.Name, y.RowsAffectedColumn?.Name, StringComparison.Ordinal)
            && SameRole(x, y, ColumnRole.Write)
            && SameRole(x, y, ColumnRole.Read)
            && SameRole(x, y, ColumnRole.Condition);
    }

    /// <inheritdoc />
    public int GetHashCode(IReadOnlyModificationCommand obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var hash = new HashCode();
        hash.Add((int)obj.EntityState);
        hash.Add(obj.TableName, StringComparer.Ordinal);
        hash.Add(obj.Schema, StringComparer.Ordinal);
        hash.Add(obj.RowsAffectedColumn?.Name, StringComparer.Ordinal);

        AddRole(ref hash, obj, ColumnRole.Write);
        AddRole(ref hash, obj, ColumnRole.Read);
        AddRole(ref hash, obj, ColumnRole.Condition);

        return hash.ToHashCode();
    }

    private static bool HasRole(IColumnModification column, ColumnRole role)
        => role switch
        {
            ColumnRole.Write => column.IsWrite,
            ColumnRole.Read => column.IsRead,
            _ => column.IsCondition
        };

    /// <summary>
    ///     Whether both commands carry the same column names in <paramref name="role" />, in the
    ///     same order.
    /// </summary>
    /// <remarks>
    ///     Order matters for writes because a bulk copy is a positional stream, and is compared for
    ///     the other roles too rather than being specially excused — two commands whose conditions
    ///     appear in different orders would generate different SQL.
    /// </remarks>
    private static bool SameRole(
        IReadOnlyModificationCommand x,
        IReadOnlyModificationCommand y,
        ColumnRole role)
    {
        var left = x.ColumnModifications;
        var right = y.ColumnModifications;

        int i = 0, j = 0;

        while (true)
        {
            while (i < left.Count && !HasRole(left[i], role))
            {
                i++;
            }

            while (j < right.Count && !HasRole(right[j], role))
            {
                j++;
            }

            if (i == left.Count || j == right.Count)
            {
                // Both must run out together; one having a column left over is a different shape.
                return i == left.Count && j == right.Count;
            }

            if (!string.Equals(left[i].ColumnName, right[j].ColumnName, StringComparison.Ordinal))
            {
                return false;
            }

            i++;
            j++;
        }
    }

    private static void AddRole(
        ref HashCode hash,
        IReadOnlyModificationCommand command,
        ColumnRole role)
    {
        var columns = command.ColumnModifications;

        for (var i = 0; i < columns.Count; i++)
        {
            if (HasRole(columns[i], role))
            {
                hash.Add(columns[i].ColumnName, StringComparer.Ordinal);
            }
        }

        // Separates the roles, so a column written in one command and read in another cannot hash
        // the same as the reverse.
        hash.Add((int)role);
    }

    private enum ColumnRole
    {
        Write,
        Read,
        Condition
    }
}
