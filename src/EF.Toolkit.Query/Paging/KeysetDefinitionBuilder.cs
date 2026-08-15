using System.Linq.Expressions;
using EFToolkit.Query.Sorting;

namespace EFToolkit.Query.Paging;

/// <summary>Declares the ordering a keyset page walks along.</summary>
/// <typeparam name="T">The element type being ordered.</typeparam>
public class KeysetDefinitionBuilder<T>
{
    private readonly List<SortTerm> _components = [];
    private bool _allowNonUniqueKey;
    private bool _allowConvertedKey;

    /// <summary>Adds an ascending component.</summary>
    /// <typeparam name="TKey">The key's type.</typeparam>
    /// <param name="keySelector">Selects the key. Must be a plain property access.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual KeysetDefinitionBuilder<T> Ascending<TKey>(Expression<Func<T, TKey>> keySelector)
        => Add(keySelector, SortDirection.Ascending);

    /// <summary>Adds a descending component.</summary>
    /// <typeparam name="TKey">The key's type.</typeparam>
    /// <param name="keySelector">Selects the key. Must be a plain property access.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual KeysetDefinitionBuilder<T> Descending<TKey>(Expression<Func<T, TKey>> keySelector)
        => Add(keySelector, SortDirection.Descending);

    /// <summary>
    ///     Accepts an ordering that is not provably total, suppressing the refusal that would
    ///     otherwise apply.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     Only correct when something outside the model guarantees the trailing component is unique
    ///     — a column with a unique constraint the EF model does not know about, for instance. With
    ///     genuinely tied rows the page boundary falls in an arbitrary place among them, and rows are
    ///     returned twice or not at all.
    /// </remarks>
    public virtual KeysetDefinitionBuilder<T> AllowNonUniqueKey()
    {
        _allowNonUniqueKey = true;
        return this;
    }

    /// <summary>
    ///     Accepts components backed by a value converter, suppressing the refusal that would
    ///     otherwise apply.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     Only correct when the conversion preserves order — a <c>DateOnly</c> stored as an ISO
    ///     string does; an <c>enum</c> stored as its name does not, because the database sorts
    ///     <c>"Cancelled"</c> before <c>"Placed"</c> whatever the enum's numbering says. Both the
    ///     comparison and the <c>ORDER BY</c> run against the stored value, so an order-losing
    ///     conversion pages along a sequence the caller never asked for.
    /// </remarks>
    public virtual KeysetDefinitionBuilder<T> AllowConvertedKey()
    {
        _allowConvertedKey = true;
        return this;
    }

    /// <summary>Produces the definition.</summary>
    /// <returns>The built definition.</returns>
    /// <exception cref="QueryNotSupportedException">
    ///     The definition has no components, or a component cannot be paged along correctly.
    /// </exception>
    protected internal virtual KeysetDefinition<T> Build()
    {
        if (_components.Count == 0)
        {
            throw new QueryNotSupportedException(
                $"The keyset definition for {typeof(T).Name} has no components, so there is no "
                + "ordering to page along. Add at least one with Ascending(x => x.Property), ending "
                + "with a unique column such as the primary key.");
        }

        foreach (var component in _components)
        {
            Validate(component);
        }

        return new KeysetDefinition<T>(_components, _allowNonUniqueKey, _allowConvertedKey);
    }

    private static void Validate(SortTerm component)
    {
        if (component.PropertyPath is null)
        {
            throw new QueryNotSupportedException(
                $"A keyset component for {typeof(T).Name} is a computed expression rather than a "
                + "property access. Keyset pagination has to read the same value back off a "
                + "materialised row to build the next cursor, and to check the column against the "
                + "model, so each component must be a plain property such as x => x.PlacedAt.");
        }

        // Nullable value types are refusable without a model, and worth refusing early: the mistake
        // is usually "order by the optional date" and it is much cheaper to hear about at startup.
        if (Nullable.GetUnderlyingType(component.KeyType) is { } underlying)
        {
            throw new QueryNotSupportedException(
                $"Keyset component '{component.PropertyPath}' is {underlying.Name}?, which is "
                + "nullable. Databases disagree about where NULLs sort — SQL Server puts them first "
                + "ascending, PostgreSQL puts them last — and a comparison against NULL is neither "
                + "true nor false, so every row with a NULL there would be skipped. Page along a "
                + "non-nullable column, or project a substitute value with a computed column.");
        }

        if (!KeysetValueCodec.IsSupported(component.KeyType))
        {
            throw new QueryNotSupportedException(
                $"Keyset component '{component.PropertyPath}' is {component.KeyType.Name}, which a "
                + "cursor cannot carry because it has no round-trippable text form. Page along a "
                + "column of a primitive, string, Guid, date, time or byte-array type.");
        }
    }

    private KeysetDefinitionBuilder<T> Add<TKey>(
        Expression<Func<T, TKey>> keySelector,
        SortDirection direction)
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        var term = SortTerm.Create($"[{_components.Count}]", keySelector, direction);

        foreach (var existing in _components)
        {
            if (existing.PropertyPath is not null && existing.PropertyPath == term.PropertyPath)
            {
                throw new QueryNotSupportedException(
                    $"Keyset component '{term.PropertyPath}' is named more than once. A column can "
                    + "only order the result one way, so the later occurrence would never break a tie "
                    + "the earlier one had not already broken.");
            }
        }

        _components.Add(term);
        return this;
    }
}
