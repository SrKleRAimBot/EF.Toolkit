using System.Reflection;
using EFToolkit.Bulk.Execution;
using Npgsql;

namespace EFToolkit.Bulk.PostgreSQL.Execution;

/// <summary>
///     Writes one column's values into a binary <c>COPY</c> stream, resolving how to do it once per
///     column rather than once per value.
/// </summary>
/// <remarks>
///     <para>
///         The straightforward call is <c>WriteAsync(value, storeTypeName)</c>, which makes Npgsql
///         look up a type handler by name for <em>every value written</em>. Across a hundred
///         thousand rows and five columns that is half a million lookups, and it dominated the copy.
///     </para>
///     <para>
///         Passing a concrete <c>T</c> instead lets Npgsql resolve through its per-type cache. The
///         catch is that inference reads the CLR type, not the column, so it is only equivalent
///         where the two agree — <c>DateTime</c> maps to <c>timestamptz</c> or <c>timestamp</c>
///         depending on the value's <see cref="DateTimeKind" />, which a column declared the other
///         way would reject. So the typed path is taken only for pairings verified to match, and
///         anything else keeps the by-name call rather than risking a wrong wire format.
///     </para>
/// </remarks>
internal sealed class NpgsqlColumnWriter
{
    private static readonly MethodInfo TypedWriter =
        typeof(NpgsqlColumnWriter).GetMethod(
            nameof(WriteTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    ///     CLR types whose inferred PostgreSQL type is unambiguous, mapped to the store types they
    ///     are safe to write for.
    /// </summary>
    private static readonly Dictionary<Type, string[]> SafeStoreTypes = new()
    {
        [typeof(int)] = ["integer"],
        [typeof(long)] = ["bigint"],
        [typeof(short)] = ["smallint"],
        [typeof(bool)] = ["boolean"],
        [typeof(decimal)] = ["numeric"],
        [typeof(double)] = ["double precision"],
        [typeof(float)] = ["real"],
        [typeof(Guid)] = ["uuid"],
        [typeof(byte[])] = ["bytea"],

        // text and character varying share a binary representation, so either is safe.
        [typeof(string)] = ["text", "character varying"],

        // Only the timestamptz direction: Npgsql infers that for a UTC DateTime, and infers plain
        // timestamp for any other Kind, so a column declared without a time zone must not take
        // this path.
        [typeof(DateTime)] = ["timestamp with time zone"],
        [typeof(DateTimeOffset)] = ["timestamp with time zone"],

        // Unambiguous in both directions: DateOnly and TimeOnly each map to exactly one
        // PostgreSQL type, with no Kind or offset to change the inference.
        [typeof(DateOnly)] = ["date"],
        [typeof(TimeOnly)] = ["time without time zone"]
    };

    /// <summary>
    ///     Whether Npgsql's inference from the CLR type is known to agree with the column's
    ///     declared store type.
    /// </summary>
    /// <remarks>
    ///     Shared with the fused writer, which needs the same guarantee for the same reason: a
    ///     typed write commits to a wire format, and being wrong about it corrupts data rather
    ///     than failing.
    /// </remarks>
    public static bool IsSafeToInferType(BulkColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);

        return column.TypeMapping?.StoreTypeNameBase is { } storeType
            && SafeStoreTypes.TryGetValue(column.ProviderClrType, out var safe)
            && safe.Contains(storeType, StringComparer.OrdinalIgnoreCase);
    }

    private readonly Func<NpgsqlBinaryImporter, object, CancellationToken, Task>? _typed;
    private readonly string? _storeType;

    private NpgsqlColumnWriter(
        Func<NpgsqlBinaryImporter, object, CancellationToken, Task>? typed,
        string? storeType)
    {
        _typed = typed;
        _storeType = storeType;
    }

    /// <summary>Builds the writer for <paramref name="column" />.</summary>
    public static NpgsqlColumnWriter For(BulkColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);

        var storeType = column.TypeMapping?.StoreTypeNameBase;

        if (IsSafeToInferType(column))
        {
            var typed = TypedWriter
                .MakeGenericMethod(column.ProviderClrType)
                .CreateDelegate<Func<NpgsqlBinaryImporter, object, CancellationToken, Task>>();

            return new NpgsqlColumnWriter(typed, storeType);
        }

        return new NpgsqlColumnWriter(typed: null, storeType);
    }

    /// <summary>Writes <paramref name="value" />, or a null, to the copy stream.</summary>
    public Task WriteAsync(
        NpgsqlBinaryImporter importer,
        object? value,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            return importer.WriteNullAsync(cancellationToken);
        }

        if (_typed is not null)
        {
            return _typed(importer, value, cancellationToken);
        }

        return _storeType is not null
            ? importer.WriteAsync(value, _storeType, cancellationToken)
            : importer.WriteAsync(value, cancellationToken);
    }

    private static Task WriteTypedAsync<T>(
        NpgsqlBinaryImporter importer,
        object value,
        CancellationToken cancellationToken)
        => importer.WriteAsync((T)value, cancellationToken);
}
