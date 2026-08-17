using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Bulk.Tests.Execution;

/// <summary>
///     A model whose properties cover the ways a column's declared CLR type and the type a driver
///     hands back can differ.
/// </summary>
/// <remarks>
///     Never connected to: everything here is built from the model, and the reader the tests use is
///     a stand-in for a driver anyway.
/// </remarks>
internal static class ColumnModel
{
    /// <summary>The before-image columns for <see cref="Shift" />, keyed by property name.</summary>
    public static Dictionary<string, BulkColumnInfo> Columns()
    {
        using var context = new ShiftContext();

        var entityType = context.Model.FindEntityType(typeof(Shift))!;

        return BulkBeforeImages.ColumnsFor(entityType)
            .Where(c => c.Property is not null)
            .ToDictionary(c => c.Property!.Name, StringComparer.Ordinal);
    }

    /// <summary>One before-image column, by property name.</summary>
    public static BulkColumnInfo Column(string property) => Columns()[property];

    /// <summary>The entity type itself, for the tests that read metadata off it.</summary>
    public static IEntityType EntityType()
    {
        using var context = new ShiftContext();
        return context.Model.FindEntityType(typeof(Shift))!;
    }

    internal sealed class Shift
    {
        public int Id { get; set; }

        /// <summary>Declared as an offset; SQL Server and Npgsql each read some column as another.</summary>
        public DateTimeOffset RecordedAt { get; set; }

        /// <summary>Declared as a date; SQL Server reads <c>date</c> as a <see cref="DateTime" />.</summary>
        public DateOnly Date { get; set; }

        /// <summary>An enum over an integer column, which no driver produces directly.</summary>
        public Grade Grade { get; set; }

        /// <summary>Converted to text, so the provider type is the one to ask the driver for.</summary>
        public ShiftCode Code { get; set; }

        public string? Note { get; set; }
    }

    internal enum Grade
    {
        Standard,
        Premium,
    }

    /// <summary>A strongly-typed value stored as text.</summary>
    internal readonly record struct ShiftCode(string Value);

    private sealed class ShiftContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            // Never opened: the columns come from the model alone.
            => optionsBuilder.UseSqlServer("Server=none;Database=none");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Shift>()
                .Property(s => s.Code)
                .HasConversion(code => code.Value, value => new ShiftCode(value));
    }
}
