using EFToolkit.Bulk.Api;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Api;

/// <summary>
///     Plan construction, and specifically the accessors it hands out.
/// </summary>
/// <remarks>
///     Compiled accessors are cached against the <c>IProperty</c> alone. An inherited property is
///     one <c>IProperty</c> shared by every type in the hierarchy, so an accessor that closed over
///     the entity type its plan was built for would be handed to the wrong CLR type the moment a
///     second type in that hierarchy was written.
/// </remarks>
public class BulkEntityPlanTests
{
    [Fact]
    public void An_inherited_property_reads_from_either_sibling_whichever_was_planned_first()
    {
        using var context = new HierarchyContext();

        var person = context.Model.FindEntityType(typeof(Person))!;
        var company = context.Model.FindEntityType(typeof(Company))!;

        // Person first, so its plan is the one that populates the shared accessor cache.
        var personPlan = BulkEntityPlan.For(person, EntityState.Added);
        var companyPlan = BulkEntityPlan.For(company, EntityState.Added);

        Read(personPlan, nameof(Party.Name), new Person { Name = "Ada" }).ShouldBe("Ada");
        Read(companyPlan, nameof(Party.Name), new Company { Name = "Acme" }).ShouldBe("Acme");
    }

    [Fact]
    public void An_inherited_key_is_written_back_to_either_sibling()
    {
        using var context = new HierarchyContext();

        var personPlan = BulkEntityPlan.For(
            context.Model.FindEntityType(typeof(Person))!, EntityState.Added);
        var companyPlan = BulkEntityPlan.For(
            context.Model.FindEntityType(typeof(Company))!, EntityState.Added);

        var person = new Person();
        var company = new Company();

        Write(personPlan, nameof(Party.Id), person, 7);
        Write(companyPlan, nameof(Party.Id), company, 9);

        person.Id.ShouldBe(7);
        company.Id.ShouldBe(9);
    }

    private static object? Read(BulkEntityPlan plan, string property, object entity)
        => plan.Getters[Ordinal(plan, property)](entity);

    private static void Write(BulkEntityPlan plan, string property, object entity, object? value)
    {
        var setter = plan.Setters[Ordinal(plan, property)];
        setter.ShouldNotBeNull();
        setter(entity, value);
    }

    private static int Ordinal(BulkEntityPlan plan, string property)
    {
        for (var i = 0; i < plan.Columns.Count; i++)
        {
            if (plan.Columns[i].Property?.Name == property)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"'{property}' is not in the plan.");
    }

    private abstract class Party
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        // Mapped explicitly below: the discriminator is a shadow property by default, and the
        // explicit API refuses those because it reads values from the entities themselves.
        public string Kind { get; set; } = string.Empty;
    }

    private sealed class Person : Party
    {
        public string? Nickname { get; set; }
    }

    private sealed class Company : Party
    {
        public string? Registration { get; set; }
    }

    private sealed class HierarchyContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            // Never opened: the plan is built from the model alone.
            => optionsBuilder.UseSqlServer("Server=none;Database=none");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Party>()
                .HasDiscriminator(p => p.Kind)
                .HasValue<Person>("person")
                .HasValue<Company>("company");
    }
}
