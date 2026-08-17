using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq.Expressions;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     Reads one field of a result set in the CLR type the model declares for it.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="DbDataReader.GetValue" /> hands back the driver's default CLR type for the
///         store type, which is not always the type the property is declared as. A provider plugin
///         is the clearest case: with Npgsql's NodaTime support registered, <c>timestamptz</c> reads
///         as an <c>Instant</c> whether the property is an <c>Instant</c>, a <c>DateTime</c> or an
///         <c>OffsetDateTime</c>, and <c>interval</c> reads as a <c>Period</c> even where the
///         property is a <c>Duration</c>. It happens without plugins too: Npgsql reads
///         <c>timestamptz</c> as a <see cref="DateTime" /> for a <see cref="DateTimeOffset" />
///         property, and SQL Server reads <c>date</c> as a <see cref="DateTime" /> for a
///         <see cref="DateOnly" /> one.
///     </para>
///     <para>
///         Reconciling that afterwards is not possible in general — <c>Convert.ChangeType</c> needs
///         both sides to cooperate, and most of these types do not — so the type is asked for up
///         front instead, through <c>GetFieldValue&lt;T&gt;</c>, which is how EF Core itself reads a
///         mapped column. The driver then does the conversion it already knows how to do.
///     </para>
///     <para>
///         Where a driver declines the requested type the raw value is read instead and reconciled
///         downstream, which is what keeps the shapes that never needed this working: an <c>int</c>
///         column feeding an enum property, or a <c>bigint</c> sequence feeding an <c>int</c> key.
///     </para>
/// </remarks>
internal static class BulkValueReader
{
    private static readonly ConcurrentDictionary<Type, Func<DbDataReader, int, object>> Readers = new();

    /// <summary>
    ///     Field-type/target-type pairs a driver has already refused, so the refusal costs one
    ///     exception per shape rather than one per value.
    /// </summary>
    /// <remarks>
    ///     Keyed by the reader's own type as well, because what a driver can produce is the driver's
    ///     answer to give: two providers in one process may well differ on the same pair.
    /// </remarks>
    private static readonly ConcurrentDictionary<(Type Reader, Type Field, Type Target), bool> Refused
        = new();

    /// <summary>Reads a field, or <see langword="null" /> when the database holds no value.</summary>
    /// <param name="reader">The reader positioned on the row.</param>
    /// <param name="ordinal">The field to read.</param>
    /// <param name="column">The column the field belongs to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async ValueTask<object?> ReadAsync(
        DbDataReader reader,
        int ordinal,
        BulkColumnInfo column,
        CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
            ? null
            : Read(reader, ordinal, column);

    /// <summary>Reads a field known not to be null.</summary>
    /// <param name="reader">The reader positioned on the row.</param>
    /// <param name="ordinal">The field to read.</param>
    /// <param name="column">The column the field belongs to.</param>
    public static object Read(DbDataReader reader, int ordinal, BulkColumnInfo column)
    {
        var target = column.ProviderClrType;

        // Nothing to ask for: an unmapped column is whatever the driver says it is.
        if (target == typeof(object))
        {
            return reader.GetValue(ordinal);
        }

        var field = reader.GetFieldType(ordinal);

        // Already the declared type, or an enum the model maps natively -- a driver that produces
        // such an enum was caught by the test before this one, and a driver that does not is not
        // going to start because it was asked. Either way its underlying value converts cleanly.
        if (field == target || target.IsEnum)
        {
            return reader.GetValue(ordinal);
        }

        var shape = (reader.GetType(), field, target);

        if (Refused.ContainsKey(shape))
        {
            return reader.GetValue(ordinal);
        }

        try
        {
            return Readers.GetOrAdd(target, Compile)(reader, ordinal);
        }
        catch (Exception exception) when (exception is InvalidCastException or NotSupportedException)
        {
            // The driver cannot produce this type from this field. Fall back to the raw value; if
            // it cannot be reconciled either, the column reports that with the detail to act on.
            //
            // Only these two, and deliberately: they are how a driver says "not that type", and
            // nothing else. A reader that is closed or positioned on no row raises
            // InvalidOperationException, which is a fault in the caller rather than an answer about
            // types — swallowing it would both hide the fault and record this shape as unreadable
            // for the rest of the process.
            Refused[shape] = true;
            return reader.GetValue(ordinal);
        }
    }

    /// <summary>Builds a non-generic call to <c>GetFieldValue&lt;T&gt;</c> for one type.</summary>
    /// <remarks>
    ///     Compiled rather than reflected per value, and cached per type: a bulk read runs this once
    ///     per row per column, which is exactly where reflection would show up.
    /// </remarks>
    private static Func<DbDataReader, int, object> Compile(Type target)
    {
        var method = typeof(DbDataReader)
            .GetMethod(nameof(DbDataReader.GetFieldValue))!
            .MakeGenericMethod(target);

        var reader = Expression.Parameter(typeof(DbDataReader), "reader");
        var ordinal = Expression.Parameter(typeof(int), "ordinal");

        return Expression
            .Lambda<Func<DbDataReader, int, object>>(
                Expression.Convert(Expression.Call(reader, method, ordinal), typeof(object)),
                reader,
                ordinal)
            .Compile();
    }
}
