using System.Text.Json;
using EFToolkit.Audit.Api;
using EFToolkit.Audit.Equivalence.Infrastructure;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Equivalence;

/// <summary>
///     What a <c>SaveChanges()</c> produces in the audit table.
/// </summary>
/// <remarks>
///     Bound to each engine by a thin sealed subclass, so the scenarios are written once and run
///     against every database the packages support.
/// </remarks>
public abstract class AuditCaptureTests(AuditDatabaseFixture fixture)
{
    [Fact]
    public async Task Records_an_insert_with_every_captured_column()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            context.Products.Add(NewProduct("SKU-1"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var entry = (await AuditSnapshot.ReadAsync(fixture)).ShouldHaveSingleItem();

        entry.EntityType.ShouldBe(nameof(Product));
        entry.Operation.ShouldBe((int)AuditOperation.Insert);
        entry.Source.ShouldBe(AuditSources.SaveChanges);

        var payload = Payload(entry);
        payload.GetProperty("op").GetString().ShouldBe("insert");
        payload.GetProperty("new").GetProperty("Name").GetString().ShouldBe("Widget");
        payload.GetProperty("new").GetProperty("Sku").GetString().ShouldBe("SKU-1");
    }

    [Fact]
    public async Task Records_the_key_the_database_generated()
    {
        await ResetAsync();

        int id;

        await using (var context = fixture.CreateContext())
        {
            var product = NewProduct("SKU-1");
            context.Products.Add(product);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = product.Id;
        }

        id.ShouldBeGreaterThan(0);

        var entry = (await AuditSnapshot.ReadAsync(fixture)).ShouldHaveSingleItem();

        // The value is a placeholder when the payload is captured, so this is the assertion that
        // says capture really is split across the two interception points.
        entry.EntityKey.ShouldBe(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Payload(entry).GetProperty("key").GetProperty("Id").GetInt32().ShouldBe(id);
    }

    [Fact]
    public async Task Records_only_the_columns_an_update_actually_changed()
    {
        await ResetAsync();

        await SeedAsync(NewProduct("SKU-1"));
        await ClearAuditAsync();

        await using (var context = fixture.CreateContext())
        {
            var product = await context.Products.SingleAsync(TestContext.Current.CancellationToken);
            product.Name = "Widget Mk II";
            product.Status = ProductStatus.Live;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var payload = Payload((await AuditSnapshot.ReadAsync(fixture)).ShouldHaveSingleItem());

        payload.GetProperty("op").GetString().ShouldBe("update");

        var changed = payload.GetProperty("changed").EnumerateArray()
            .Select(e => e.GetString()).ToList();

        changed.Count.ShouldBe(2);
        changed.ShouldContain(nameof(Product.Name));
        changed.ShouldContain(nameof(Product.Status));

        payload.GetProperty("old").GetProperty("Name").GetString().ShouldBe("Widget");
        payload.GetProperty("new").GetProperty("Name").GetString().ShouldBe("Widget Mk II");

        // Stored through a converter, so the payload has to say what the column says.
        payload.GetProperty("old").GetProperty("Status").GetString().ShouldBe("Draft");
        payload.GetProperty("new").GetProperty("Status").GetString().ShouldBe("Live");
    }

    [Fact]
    public async Task Writes_nothing_for_an_update_that_changed_nothing()
    {
        await ResetAsync();

        await SeedAsync(NewProduct("SKU-1"));
        await ClearAuditAsync();

        await using (var context = fixture.CreateContext())
        {
            var product = await context.Products.SingleAsync(TestContext.Current.CancellationToken);

            // Assigned, so EF marks it modified. Nothing changed, so nothing should be recorded.
            product.Name = product.Name;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Records_a_delete_with_the_row_as_it_was()
    {
        await ResetAsync();

        await SeedAsync(NewProduct("SKU-1"));
        await ClearAuditAsync();

        await using (var context = fixture.CreateContext())
        {
            var product = await context.Products.SingleAsync(TestContext.Current.CancellationToken);
            context.Products.Remove(product);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var entry = (await AuditSnapshot.ReadAsync(fixture)).ShouldHaveSingleItem();
        entry.Operation.ShouldBe((int)AuditOperation.Delete);

        var payload = Payload(entry);
        payload.GetProperty("op").GetString().ShouldBe("delete");
        payload.GetProperty("old").GetProperty("Sku").GetString().ShouldBe("SKU-1");
    }

    [Fact]
    public async Task Leaves_an_unregistered_entity_type_alone()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            context.Sessions.Add(new Session { Token = "abc" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Masks_a_masked_column_and_omits_an_excluded_one()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            var product = NewProduct("SKU-1");
            product.CardNumber = "4111111111111111";
            product.InternalNotes = "do not ship";
            context.Products.Add(product);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var values = Payload((await AuditSnapshot.ReadAsync(fixture)).ShouldHaveSingleItem())
            .GetProperty("new");

        values.GetProperty(nameof(Product.CardNumber)).GetString().ShouldBe("***");
        values.TryGetProperty(nameof(Product.InternalNotes), out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Honours_registration_and_masking_stated_by_attribute()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            context.ApiKeys.Add(new ApiKey { Name = "ci", Secret = "s3cret", Scratch = "junk" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var entry = (await AuditSnapshot.ReadAsync(fixture)).ShouldHaveSingleItem();
        entry.EntityType.ShouldBe(nameof(ApiKey));

        var values = Payload(entry).GetProperty("new");
        values.GetProperty(nameof(ApiKey.Name)).GetString().ShouldBe("ci");
        values.GetProperty(nameof(ApiKey.Secret)).GetString().ShouldBe("***");
        values.TryGetProperty(nameof(ApiKey.Scratch), out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Records_the_tenant_from_the_entity()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            var product = NewProduct("SKU-1");
            product.TenantId = "acme";
            context.Products.Add(product);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldHaveSingleItem().TenantId.ShouldBe("acme");
    }

    [Fact]
    public async Task Records_the_actor_and_reason_from_an_ambient_scope()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            using var scope = AuditScope
                .Begin(new AuditActor("job-7", "Nightly reprice", "service"), reason: "quarter end")
                .With("batch", 4);

            context.Products.Add(NewProduct("SKU-1"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var entry = (await AuditSnapshot.ReadAsync(fixture)).ShouldHaveSingleItem();

        entry.ActorId.ShouldBe("job-7");
        entry.ActorName.ShouldBe("Nightly reprice");
        entry.ActorType.ShouldBe("service");
        entry.CorrelationId.ShouldNotBeNull();

        var meta = Payload(entry).GetProperty("meta");
        meta.GetProperty("reason").GetString().ShouldBe("quarter end");
        meta.GetProperty("batch").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task Folds_an_owned_value_object_into_its_owner()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            context.Warehouses.Add(new Warehouse
            {
                Id = Guid.CreateVersion7(),
                Name = "North",
                TenantId = "acme",
                Address = new Address { City = "Leeds", Postcode = "LS1" },
            });

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // One entry, not two: the owned value lives in the owner's row and belongs in its entry.
        var entry = (await AuditSnapshot.ReadAsync(fixture)).ShouldHaveSingleItem();
        entry.EntityType.ShouldBe(nameof(Warehouse));

        Payload(entry).GetProperty("new").GetProperty("Address.City").GetString().ShouldBe("Leeds");
    }

    [Fact]
    public async Task Records_a_change_to_an_owned_value_alone()
    {
        await ResetAsync();

        var id = Guid.CreateVersion7();

        await using (var context = fixture.CreateContext())
        {
            context.Warehouses.Add(new Warehouse
            {
                Id = id,
                Name = "North",
                TenantId = "acme",
                Address = new Address { City = "Leeds", Postcode = "LS1" },
            });

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await ClearAuditAsync();

        await using (var context = fixture.CreateContext())
        {
            var warehouse = await context.Warehouses.SingleAsync(TestContext.Current.CancellationToken);
            warehouse.Address.City = "York";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The owner's own entry stays Unchanged in this case, so an implementation that only looked
        // at owner states would record nothing at all.
        var payload = Payload((await AuditSnapshot.ReadAsync(fixture)).ShouldHaveSingleItem());

        payload.GetProperty("changed").EnumerateArray()
            .Select(e => e.GetString())
            .ShouldContain("Address.City");

        payload.GetProperty("old").GetProperty("Address.City").GetString().ShouldBe("Leeds");
        payload.GetProperty("new").GetProperty("Address.City").GetString().ShouldBe("York");
    }

    [Fact]
    public async Task Renders_a_composite_key_unambiguously()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            // The separator inside a component is what a naive join gets wrong: "a|b" + "c" and
            // "a" + "b|c" would render the same string and cross two rows' histories.
            context.Assignments.Add(new Assignment { ProductSku = "a|b", UserId = "c", Role = "r" });
            context.Assignments.Add(new Assignment { ProductSku = "a", UserId = "b|c", Role = "r" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var keys = (await AuditSnapshot.ReadAsync(fixture)).Select(e => e.EntityKey).ToList();

        keys.Count.ShouldBe(2);
        keys.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Stamps_every_entry_of_one_save_with_the_same_correlation_id()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext())
        {
            using var scope = AuditScope.Begin("importer");

            context.Products.Add(NewProduct("SKU-1"));
            context.ApiKeys.Add(new ApiKey { Name = "ci", Secret = "x" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);

        entries.Count.ShouldBe(2);
        entries.Select(e => e.CorrelationId).Distinct().Count().ShouldBe(1);
    }

    /// <summary>Empties every table, audit entries included.</summary>
    protected Task ResetAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        return fixture.ResetAsync();
    }

    /// <summary>Removes the entries a seeding step produced, so a scenario starts clean.</summary>
    protected async Task ClearAuditAsync()
    {
        var table = $"{fixture.Quote(Audit.Configuration.AuditOptions.DefaultSchema)}."
            + $"{fixture.Quote(Audit.Configuration.AuditOptions.DefaultTableName)}";

        await using var connection = fixture.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {table}";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Writes rows without auditing them, so a scenario can start from existing data.</summary>
    protected async Task SeedAsync(params Product[] products)
    {
        await using var context = fixture.CreateContext(auditing: false);
        context.Products.AddRange(products);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The payload of an entry, parsed.</summary>
    protected static JsonElement Payload(AuditRow entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return JsonDocument.Parse(entry.Changes).RootElement;
    }

    /// <summary>A product with every column set to something recognisable.</summary>
    protected static Product NewProduct(string sku) => new()
    {
        Sku = sku,
        Name = "Widget",
        Price = 9.99m,
        Status = ProductStatus.Draft,
        TenantId = "acme",
    };
}
