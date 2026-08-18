using System.ComponentModel;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

namespace EFToolkit.Query.Tests.Infrastructure;

/// <summary>
///     A strongly typed id stored as <c>text</c>: the CLR type a cursor knows nothing about, over a
///     stored type it knows well.
/// </summary>
/// <remarks>
///     Written the way one actually is: a record struct wrapping the text, with no ordering of its
///     own. The database orders the column it is stored in, which is what the page comparison ends up
///     running against.
/// </remarks>
public readonly record struct WorkerId(string Value);

/// <summary>
///     Stored as itself, because no provider here maps it — so nothing can render it in a cursor.
/// </summary>
public readonly record struct Rank(int Value);

/// <summary>Carried by a <see cref="TypeConverter" /> that drops its fraction.</summary>
[TypeConverter(typeof(TruncatingConverter))]
public readonly record struct Metres(decimal Value);

/// <summary>Renders whole metres only, so a boundary of 1.5 comes back as 1.</summary>
public sealed class TruncatingConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        => destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => new Metres(decimal.Parse((string)value, CultureInfo.InvariantCulture));

    public override object? ConvertTo(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object? value,
        Type destinationType)
        => destinationType == typeof(string)
            ? decimal.Truncate(((Metres)value!).Value).ToString(CultureInfo.InvariantCulture)
            : base.ConvertTo(context, culture, value, destinationType);
}

/// <summary>
///     Every column a keyset ordering could reasonably want and the CLR type cannot describe: an id
///     stored through a value converter, and NodaTime columns the provider maps natively.
/// </summary>
public class Worker
{
    public WorkerId Id { get; set; }

    public string FullName { get; set; } = "";

    /// <summary>Mapped to <c>timestamptz</c> by the Npgsql NodaTime plugin, with no converter.</summary>
    public Instant HiredAt { get; set; }

    /// <summary>Mapped to <c>date</c>.</summary>
    public LocalDate StartsOn { get; set; }
}

/// <summary>Not mapped: a projection, which is the case with no model to read anything off.</summary>
public class WorkerSummary
{
    public Rank Rank { get; set; }

    public Metres Distance { get; set; }
}

/// <summary>
///     Builds contexts over <see cref="Worker" />, on PostgreSQL because it is the provider that maps
///     NodaTime. As with <see cref="TestModel" /> the connection string never connects — every test
///     here reads the model and builds expressions.
/// </summary>
public static class StoredTypeModel
{
    public static StoredTypeContext Context()
        => new(new DbContextOptionsBuilder<StoredTypeContext>()
            .UseNpgsql("Host=none;Database=none", static o => o.UseNodaTime())
            .UseQueryHelpers()
            .ReplaceService<IModelCacheKeyFactory, PerInstanceStoredTypeModelCacheKeyFactory>()
            .Options);
}

public class StoredTypeContext(DbContextOptions<StoredTypeContext> options) : DbContext(options)
{
    internal object ModelCacheKey { get; } = new();

    public DbSet<Worker> Workers => Set<Worker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Worker>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasConversion(new ValueConverter<WorkerId, string>(
                    id => id.Value,
                    value => new WorkerId(value)));
            b.Property(x => x.FullName).HasMaxLength(128).IsRequired();
        });
    }
}

public sealed class PerInstanceStoredTypeModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => (((StoredTypeContext)context).ModelCacheKey, designTime);
}
