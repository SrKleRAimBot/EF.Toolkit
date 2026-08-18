using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using EFToolkit.Query.Sorting;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Query.Paging;

/// <summary>Builds <see cref="KeysetDefinition{T}" /> instances.</summary>
public static class KeysetDefinition
{
    /// <summary>Declares the ordering a keyset page walks along.</summary>
    /// <typeparam name="T">The element type being ordered.</typeparam>
    /// <param name="configure">Declares the ordering components, last one unique.</param>
    /// <returns>The definition.</returns>
    /// <example>
    ///     <code>
    ///     static readonly KeysetDefinition&lt;Order&gt; ByNewest = KeysetDefinition.For&lt;Order&gt;(k => k
    ///         .Descending(o => o.PlacedAt)
    ///         .Ascending(o => o.Id));
    ///     </code>
    /// </example>
    /// <remarks>
    ///     A definition is immutable and compiles its key accessors once, so declare it as a static
    ///     field rather than rebuilding it per request.
    /// </remarks>
    public static KeysetDefinition<T> For<T>(Action<KeysetDefinitionBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new KeysetDefinitionBuilder<T>();
        configure(builder);
        return builder.Build();
    }
}

/// <summary>
///     An ordering that a keyset page can walk along: a list of columns, each with a direction, the
///     last of which is unique.
/// </summary>
/// <typeparam name="T">The element type being ordered.</typeparam>
public sealed class KeysetDefinition<T>
{
    private readonly bool _allowNonUniqueKey;
    private readonly bool _allowConvertedKey;
    private readonly Func<T, object?>[] _accessors;

    internal KeysetDefinition(
        IReadOnlyList<SortTerm> components,
        bool allowNonUniqueKey,
        bool allowConvertedKey)
    {
        Components = components;
        _allowNonUniqueKey = allowNonUniqueKey;
        _allowConvertedKey = allowConvertedKey;
        _accessors = components.Select(Compile).ToArray();
        Fingerprint = ComputeFingerprint(components);
    }

    /// <summary>
    ///     Identifies this ordering. Carried in every cursor, so replaying one against a different
    ///     ordering is refused rather than silently answered.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>The columns, in priority order.</summary>
    public IReadOnlyList<string> ColumnPaths => Components.Select(c => c.PropertyPath!).ToArray();

    internal IReadOnlyList<SortTerm> Components { get; }

    /// <summary>Orders <paramref name="source" /> by this definition.</summary>
    /// <param name="source">The query to order.</param>
    /// <param name="backward">Whether to reverse every component.</param>
    /// <returns>The ordered query.</returns>
    public IOrderedQueryable<T> Order(IQueryable<T> source, bool backward = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        var terms = backward
            ? Components.Select(static c => c.Reversed()).ToArray()
            : Components;

        return Ordering.Apply(source, terms);
    }

    /// <summary>Builds the cursor pointing at <paramref name="row" />.</summary>
    internal KeysetCursor CursorFor(T row, KeysetPageDirection direction, KeysetBinding binding)
    {
        var values = new string[Components.Count];

        for (var i = 0; i < Components.Count; i++)
        {
            var value = _accessors[i](row)
                ?? throw new QueryNotSupportedException(
                    $"The keyset column '{Components[i].PropertyPath}' read back as null on a row that "
                    + "was just returned. A keyset ordering cannot include a nullable column, so the "
                    + "model and the CLR type disagree — check for a column mapped as required whose "
                    + "property is not.");

            values[i] = binding.Encode(i, value);
        }

        return new KeysetCursor(Fingerprint, direction, values);
    }

    /// <summary>Converts a cursor's rendered values back to the component key types.</summary>
    /// <exception cref="QueryNotSupportedException">The cursor does not belong to this ordering.</exception>
    internal IReadOnlyList<object?> Decode(KeysetCursor cursor, KeysetBinding binding)
    {
        if (cursor.Fingerprint != Fingerprint)
        {
            throw new QueryNotSupportedException(
                "This cursor was issued for a different ordering, so its values line up with columns "
                + "that are not the ones being paged along now. That usually means the sort changed "
                + $"between requests. Ask for the first page again. (Ordering is: "
                + $"{string.Join(", ", Components.Select(c => $"{c.PropertyPath} {c.Direction}"))}.)");
        }

        if (cursor.Values.Count != Components.Count)
        {
            throw new QueryNotSupportedException(
                $"This cursor carries {cursor.Values.Count} value(s) but the ordering has "
                + $"{Components.Count} column(s). The cursor has been altered in transit; ask for the "
                + "first page again.");
        }

        var values = new object?[Components.Count];
        for (var i = 0; i < Components.Count; i++)
        {
            values[i] = binding.Decode(i, cursor.Values[i]);
        }

        return values;
    }

    /// <summary>The predicate selecting rows past <paramref name="cursor" />.</summary>
    internal Expression<Func<T, bool>> After(KeysetCursor cursor, KeysetBinding binding)
        => KeysetPredicate.After<T>(
            Components,
            Decode(cursor, binding),
            backward: cursor.Direction == KeysetPageDirection.Backward);

    /// <summary>
    ///     Checks the ordering against the EF model, refusing the cases where the SQL comparison would
    ///     not mean what the definition says, and resolves what a cursor renders each component as.
    /// </summary>
    /// <param name="entityType">
    ///     The mapped type, or <see langword="null" /> when <typeparamref name="T" /> is not one.
    ///     Paging a projection is legitimate; the model-dependent refusals are skipped for it, and
    ///     each component binds to its own CLR type.
    /// </param>
    /// <returns>The components bound to the model, for encoding and decoding cursors.</returns>
    internal KeysetBinding ValidateAgainst(IEntityType? entityType)
    {
        var binding = KeysetBinding.For(Components, entityType);

        for (var i = 0; i < Components.Count; i++)
        {
            var component = Components[i];
            var property = entityType?.FindProperty(component.PropertyPath!);

            // A null property is a navigation, a shadow-state mismatch, a property EF does not map, or
            // a projection with no model behind it at all. There is nothing model-shaped to check;
            // the query itself will fail clearly enough if it cannot translate.
            if (property is not null)
            {
                if (property.IsNullable)
                {
                    throw new QueryNotSupportedException(
                        $"Keyset column '{component.PropertyPath}' is mapped as nullable. Databases "
                        + "disagree about where NULLs sort, and a comparison against NULL is neither "
                        + "true nor false, so every row with a NULL there would be skipped by every "
                        + "page. Page along a required column, or make this one required.");
                }

                if (!_allowConvertedKey && ConverterLosesOrder(property))
                {
                    throw new QueryNotSupportedException(
                        $"Keyset column '{component.PropertyPath}' is stored through a value converter, "
                        + "so both the ORDER BY and the page comparison run against the converted "
                        + "value, not the CLR one. An enum stored as text, for instance, sorts "
                        + "alphabetically rather than by its numbering, and the pages come back in "
                        + "that order instead. Page along an unconverted column, or call "
                        + "AllowConvertedKey() if you know this conversion preserves order.");
                }
            }

            // Checked here rather than at build time because the answer depends on the model: what a
            // cursor has to carry is the stored value, and a converted column stores something other
            // than its CLR type. The property may be a NodaTime Instant the provider maps natively, or
            // a strongly typed id stored as text — neither is anything the CLR type alone can say.
            var cursorType = binding.CursorTypeOf(i);

            if (!KeysetValueCodec.IsSupported(cursorType))
            {
                throw new QueryNotSupportedException(
                    $"Keyset column '{component.PropertyPath}' is stored as {cursorType.Name}"
                    + (cursorType == component.KeyType ? "" : $" (from {component.KeyType.Name})")
                    + ", which a cursor cannot carry because it has no round-trippable text form. Page "
                    + "along a column of a primitive, string, Guid, date, time or byte-array type, "
                    + "store this one through a value converter to one of those, or give its type a "
                    + "TypeConverter that reads its own output back.");
            }
        }

        if (entityType is not null && !_allowNonUniqueKey && !IsTotalOrdering(entityType))
        {
            throw new QueryNotSupportedException(
                $"The keyset ordering ({string.Join(", ", ColumnPaths)}) is not total: no key or unique "
                + $"index of {entityType.DisplayName()} is covered by it, so rows can tie. The page "
                + "boundary then falls in an arbitrary place among the tied rows, and a row can be "
                + "returned on two pages or on none. End the ordering with the primary key, or call "
                + "AllowNonUniqueKey() if uniqueness is guaranteed outside the model.");
        }

        return binding;
    }

    /// <summary>
    ///     Whether the column is stored through a conversion that does not carry the CLR ordering over
    ///     to the database.
    /// </summary>
    /// <remarks>
    ///     The one conversion allowed through is an enum stored as its own underlying number, which EF
    ///     applies by default and which sorts identically on both sides. Everything else is refused:
    ///     <c>ORDER BY</c> and the page comparison both run against the stored value, so an enum
    ///     written as text pages alphabetically rather than by the enum's numbering, and the caller
    ///     has no way to see it from the results.
    /// </remarks>
    private static bool ConverterLosesOrder(IProperty property)
    {
        var converter = KeysetBinding.ConverterOf(property);

        if (converter is null)
        {
            return false;
        }

        var model = Nullable.GetUnderlyingType(converter.ModelClrType) ?? converter.ModelClrType;
        var provider = Nullable.GetUnderlyingType(converter.ProviderClrType) ?? converter.ProviderClrType;

        return !(model.IsEnum && provider == Enum.GetUnderlyingType(model));
    }

    /// <summary>
    ///     Whether the components cover some key or unique index, which is what makes the ordering
    ///     total. Order does not matter — covering every column of a unique constraint is enough for
    ///     no two rows to tie on all of them.
    /// </summary>
    private bool IsTotalOrdering(IEntityType entityType)
    {
        var paths = new HashSet<string>(ColumnPaths, StringComparer.Ordinal);

        foreach (var key in entityType.GetKeys())
        {
            if (key.Properties.All(p => paths.Contains(p.Name)))
            {
                return true;
            }
        }

        foreach (var index in entityType.GetIndexes())
        {
            if (index.IsUnique && index.Properties.All(p => paths.Contains(p.Name)))
            {
                return true;
            }
        }

        return false;
    }

    private static Func<T, object?> Compile(SortTerm component)
    {
        var parameter = Expression.Parameter(typeof(T), "e");
        var body = Filtering.ParameterRebinder.Rebind(
            component.KeySelector.Body,
            component.KeySelector.Parameters[0],
            parameter);

        return Expression
            .Lambda<Func<T, object?>>(Expression.Convert(body, typeof(object)), parameter)
            .Compile();
    }

    /// <summary>
    ///     A stable identity for the ordering. Hashed rather than spelled out so the token does not
    ///     publish the column names, and computed with SHA-256 rather than
    ///     <see cref="object.GetHashCode" /> because string hashing is randomised per process — a
    ///     cursor issued by one instance has to be readable by the next one.
    /// </summary>
    private static string ComputeFingerprint(IReadOnlyList<SortTerm> components)
    {
        var builder = new StringBuilder(typeof(T).FullName);

        foreach (var component in components)
        {
            builder
                .Append('|')
                .Append(component.PropertyPath)
                .Append(':')
                .Append(component.Direction == SortDirection.Ascending ? 'a' : 'd');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
