using System.Globalization;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     Everything an executor needs to know about one column it is writing.
/// </summary>
/// <remarks>
///     Deliberately not tied to <see cref="Microsoft.EntityFrameworkCore.Update.IColumnModification" />:
///     the explicit bulk API reads values straight off entities and never builds modification
///     commands, which is exactly why it can outrun transparent mode.
/// </remarks>
public sealed class BulkColumnInfo
{
    /// <summary>Initializes a new column descriptor.</summary>
    /// <param name="name">The database column name.</param>
    /// <param name="typeMapping">The column's type mapping, used for value conversion.</param>
    /// <param name="property">The mapped property, if any.</param>
    /// <param name="isWrite">Whether a value is supplied for this column.</param>
    /// <param name="isRead">Whether the store generates this column's value.</param>
    /// <param name="isKey">Whether the column is part of the primary key.</param>
    /// <param name="isCondition">
    ///     Whether the column takes part in the WHERE clause that locates the row — the key, plus
    ///     any concurrency tokens.
    /// </param>
    /// <param name="isInsertOnly">
    ///     Whether a merge writes this column only when it inserts the row.
    /// </param>
    public BulkColumnInfo(
        string name,
        RelationalTypeMapping? typeMapping,
        IProperty? property,
        bool isWrite,
        bool isRead,
        bool isKey,
        bool isCondition = false,
        bool isInsertOnly = false)
    {
        IsCondition = isCondition;
        Name = name;
        TypeMapping = typeMapping;
        Property = property;
        IsWrite = isWrite;
        IsRead = isRead;
        IsKey = isKey;
        IsInsertOnly = isInsertOnly;
    }

    /// <summary>The database column name.</summary>
    public string Name { get; }

    /// <summary>The column's type mapping. Carries the value converter, if there is one.</summary>
    public RelationalTypeMapping? TypeMapping { get; }

    /// <summary>The mapped property, if any.</summary>
    public IProperty? Property { get; }

    /// <summary>Whether a value is supplied for this column.</summary>
    public bool IsWrite { get; }

    /// <summary>Whether the store generates this column's value and it must be read back.</summary>
    public bool IsRead { get; }

    /// <summary>Whether the column is part of the primary key.</summary>
    public bool IsKey { get; }

    /// <summary>
    ///     Whether the column takes part in locating the row for an update or delete.
    /// </summary>
    /// <remarks>
    ///     Equal to the key for a straightforward entity, but widens to include concurrency tokens
    ///     when the model has them — which is exactly the case a bulk update has to detect and
    ///     report as a conflict rather than silently miss.
    /// </remarks>
    public bool IsCondition { get; }

    /// <summary>
    ///     Whether a merge writes this column when it inserts the row but leaves it alone when it
    ///     updates one.
    /// </summary>
    /// <remarks>
    ///     Still a written column — the insert arm needs a value for it — so it is staged and
    ///     streamed like any other; it is only dropped from the update arm's assignment list. This
    ///     is how a created-at column survives a row being merged over.
    /// </remarks>
    public bool IsInsertOnly { get; }

    /// <summary>The CLR type the driver should expect, after any value converter has run.</summary>
    public Type ProviderClrType
    {
        get
        {
            var type = TypeMapping?.Converter?.ProviderClrType
                ?? Property?.ClrType
                ?? typeof(object);

            // Nullability is expressed by a null value, not by the declared type.
            return Nullable.GetUnderlyingType(type) ?? type;
        }
    }

    /// <summary>Applies the column's value converter, if it has one.</summary>
    /// <param name="value">The CLR value.</param>
    /// <returns>The value as the provider expects it.</returns>
    public object? ToProviderValue(object? value)
        => TypeMapping?.Converter is { } converter ? converter.ConvertToProvider(value) : value;

    /// <summary>
    ///     Reverses <see cref="ToProviderValue" />, so a store-generated value lands on the entity
    ///     in the form the model declares.
    /// </summary>
    /// <remarks>
    ///     Without the converter step a generated column that has one round-trips wrongly: the
    ///     value written to the entity is whatever the provider handed back, not what the property
    ///     is declared to hold.
    /// </remarks>
    /// <param name="value">The value the database generated.</param>
    /// <returns>The value as the entity expects it.</returns>
    public object? FromProviderValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (TypeMapping?.Converter is { } converter)
        {
            // A store can widen on the way out -- a bigint for an int column -- and a converter
            // built for the declared provider type would reject that, so narrow before converting.
            return converter.ConvertFromProvider(Coerce(value, converter.ProviderClrType));
        }

        return Property?.ClrType is { } clrType ? Coerce(value, clrType) : value;
    }

    private static object Coerce(object value, Type target)
    {
        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        // Providers widen freely -- a bigint from a sequence, a decimal from an identity -- so the
        // value is narrowed back to what the target actually declares.
        return value.GetType() == underlying
            ? value
            : Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }
}
