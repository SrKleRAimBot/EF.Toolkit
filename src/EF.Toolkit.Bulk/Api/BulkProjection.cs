using System.Linq.Expressions;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Bulk.Api;

/// <summary>
///     Which columns a call writes, when the caller has narrowed them.
/// </summary>
/// <remarks>
///     <para>
///         Applies to the written set only. Read-back is the store's business — a column the
///         database generates has to come back whether or not the caller mentioned it — and the
///         condition columns are what locate the row, so narrowing either would change what the
///         statement means rather than what it writes.
///     </para>
///     <para>
///         There is deliberately no equivalent on the transparent <c>SaveChanges()</c> path. There
///         EF decides the write set from change tracking, which is already narrower than any
///         projection would be.
///     </para>
/// </remarks>
internal sealed class BulkProjection
{
    private readonly HashSet<string>? _include;
    private readonly HashSet<string>? _exclude;
    private readonly HashSet<string>? _insertOnly;

    private BulkProjection(
        HashSet<string>? include,
        HashSet<string>? exclude,
        HashSet<string>? insertOnly)
    {
        _include = include;
        _exclude = exclude;
        _insertOnly = insertOnly;
    }

    /// <summary>
    ///     Resolves the projection <paramref name="options" /> asked for, or <see langword="null" />
    ///     when it asked for none.
    /// </summary>
    /// <param name="entityType">The entity type being written.</param>
    /// <param name="options">The call's settings.</param>
    /// <param name="kind">What the database is being asked to do.</param>
    public static BulkProjection? Resolve<TEntity>(
        IEntityType entityType,
        BulkOperationOptions options,
        BulkOperationKind kind)
        where TEntity : class
    {
        var include = Names<TEntity>(entityType, options.Include, "Include");
        var exclude = Names<TEntity>(entityType, options.Exclude, "Exclude");
        var insertOnly = Names<TEntity>(entityType, options.InsertOnly, "InsertOnly");

        if (include is not null && exclude is not null)
        {
            throw new BulkNotSupportedException(
                "Include and Exclude cannot both be used on one call: Include already states the "
                + "whole set of columns to write, so an Exclude alongside it can only agree or "
                + "contradict.");
        }

        if (insertOnly is not null && kind is not (BulkOperationKind.Merge or BulkOperationKind.Synchronize))
        {
            throw new BulkNotSupportedException(
                $"InsertOnly has no meaning for a {kind.ToString().ToLowerInvariant()}: it marks "
                + "columns that a merge writes when it inserts a row but leaves alone when it "
                + "updates one, and only a merge or a synchronise does both.");
        }

        return include is null && exclude is null && insertOnly is null
            ? null
            : new BulkProjection(include, exclude, insertOnly);
    }

    /// <summary>
    ///     Whether <paramref name="property" /> is still written, given that it otherwise would be.
    /// </summary>
    /// <param name="property">The property in question.</param>
    /// <param name="otherwise">What the operation decided before the projection was applied.</param>
    public bool Writes(IProperty property, bool otherwise)
    {
        if (!otherwise)
        {
            // A projection narrows the write set; it never widens it. Naming a store-generated
            // column in Include does not make it writable.
            return false;
        }

        if (_include is not null)
        {
            return _include.Contains(property.Name);
        }

        return _exclude is null || !_exclude.Contains(property.Name);
    }

    /// <summary>
    ///     Whether <paramref name="property" /> is written on insert but left alone on update.
    /// </summary>
    /// <param name="property">The property in question.</param>
    public bool IsInsertOnly(IProperty property)
        => _insertOnly is not null && _insertOnly.Contains(property.Name);

    /// <summary>Names the caller used, for an error that has to quote them back.</summary>
    public string Describe()
        => _include is not null
            ? Describe("Include", _include)
            : _exclude is not null
                ? Describe("Exclude", _exclude)
                : Describe("InsertOnly", _insertOnly ?? []);

    private static string Describe(string call, IEnumerable<string> names)
        => $"{call}({string.Join(", ", names.Order(StringComparer.Ordinal))})";

    private static HashSet<string>? Names<TEntity>(
        IEntityType entityType,
        LambdaExpression? selector,
        string call)
        where TEntity : class
    {
        if (selector is not Expression<Func<TEntity, object?>> typed)
        {
            return null;
        }

        return [.. MatchPropertySelector.Resolve(entityType, typed, call).Select(p => p.Name)];
    }
}
