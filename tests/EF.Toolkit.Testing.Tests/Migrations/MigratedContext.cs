using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

// Hand-written in the shape `dotnet ef migrations add` produces for SQLite, so the migration and
// pending-model-change paths run without a design-time dependency.
namespace EFToolkit.Testing.Tests.Migrations;

public sealed class Widget
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;
}

/// <summary>A context whose migrations match its model exactly.</summary>
public sealed class MigratedContext(DbContextOptions<MigratedContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();
}

[DbContext(typeof(MigratedContext))]
[Migration("20260101000000_Initial")]
public sealed class Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.CreateTable(
            name: "Widgets",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Widgets", x => x.Id));

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "Widgets");
}

[DbContext(typeof(MigratedContext))]
public sealed class MigratedContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.0");

        modelBuilder.Entity("EFToolkit.Testing.Tests.Migrations.Widget", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<string>("Name")
                .IsRequired()
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.ToTable("Widgets");
        });
    }
}

public sealed class Gadget
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string Colour { get; set; } = null!;
}

/// <summary>A context whose model has moved on since its last migration: Colour is new.</summary>
public sealed class DriftedContext(DbContextOptions<DriftedContext> options) : DbContext(options)
{
    public DbSet<Gadget> Gadgets => Set<Gadget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Gadget>().HasIndex(g => g.Name);
}

[DbContext(typeof(DriftedContext))]
public sealed class DriftedContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.0");

        modelBuilder.Entity("EFToolkit.Testing.Tests.Migrations.Gadget", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<string>("Name")
                .IsRequired()
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.ToTable("Gadgets");
        });
    }
}

/// <summary>A context with no migrations at all.</summary>
public sealed class UnmigratedContext(DbContextOptions<UnmigratedContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();
}
