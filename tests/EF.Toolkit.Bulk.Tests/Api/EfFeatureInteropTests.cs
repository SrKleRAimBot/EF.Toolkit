using EFToolkit.Bulk.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Api;

/// <summary>
///     How the explicit bulk API meets the EF Core mapping features it does not implement itself.
/// </summary>
/// <remarks>
///     <para>
///         The explicit API is the one path that does not go through EF's update pipeline: it reads
///         values off detached objects and writes columns directly. Everything EF would otherwise
///         have done on the way — spanning several tables, filling in a table-sharing type's columns,
///         putting a concurrency token in the <c>WHERE</c> clause — is therefore this library's
///         problem, and each one has a wrong answer that looks exactly like a working call.
///     </para>
///     <para>
///         So the assertions below come in pairs. Every refusal is matched by the nearest mapping
///         that must keep working, because a guard that refuses too much is its own kind of bug: it
///         would push perfectly ordinary models onto the slow path with no way to tell.
///     </para>
///     <para>
///         None of this needs a database. A plan is built from model metadata alone, and the
///         connection string below is never opened.
///     </para>
/// </remarks>
public class EfFeatureInteropTests
{
    // ---------------------------------------------------------------------------------------
    // Inheritance
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_table_per_type_derived_entity_is_refused()
    {
        // Its row spans both tables. Taking the first mapping wrote the base columns and dropped
        // every derived one, reporting success on a half-written row.
        using var context = new InteropContext();

        var error = Should.Throw<BulkNotSupportedException>(
            () => Plan<TptEmployee>(context, EntityState.Added));

        error.Message.ShouldContain("mapped to more than one table");
        error.Message.ShouldContain("TptPeople");
        error.Message.ShouldContain("TptEmployees");
    }

    [Fact]
    public void A_table_per_type_base_entity_is_allowed()
    {
        // The base type occupies exactly one table, so nothing about it is ambiguous. Refusing a
        // whole hierarchy because one end of it spans tables would be over-reach.
        using var context = new InteropContext();

        Columns(Plan<TptPerson>(context, EntityState.Added))
            .ShouldBe([nameof(TptPerson.Id), nameof(TptPerson.Name)], ignoreOrder: true);
    }

    [Fact]
    public void A_table_per_hierarchy_entity_is_allowed()
    {
        // One table, one row, every column reachable from the entity. TPH is the inheritance
        // mapping the explicit API can serve, and the sharers on that table are its own siblings —
        // which is why table sharing is judged by root type rather than by mapping count.
        using var context = new InteropContext();

        Columns(Plan<TphCard>(context, EntityState.Added))
            .ShouldContain(nameof(TphCard.Kind));

        Columns(Plan<TphCreditCard>(context, EntityState.Added))
            .ShouldContain(nameof(TphCreditCard.Limit));
    }

    [Fact]
    public void A_table_per_hierarchy_entity_with_a_shadow_discriminator_is_refused()
    {
        // The discriminator has to be written and there is nothing on the entity to read it from.
        using var context = new InteropContext();

        Should.Throw<BulkNotSupportedException>(() => Plan<ShadowDiscriminated>(context, EntityState.Added))
            .Message.ShouldContain("shadow property");
    }

    [Fact]
    public void An_entity_split_across_two_tables_is_refused()
    {
        // Entity splitting is not inheritance, but it fails for the same reason and has to be
        // caught by the same guard: one entity type, two tables, one bulk statement.
        using var context = new InteropContext();

        Should.Throw<BulkNotSupportedException>(() => Plan<SplitDocument>(context, EntityState.Added))
            .Message.ShouldContain("mapped to more than one table");
    }

    // ---------------------------------------------------------------------------------------
    // Table sharing
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void An_owner_sharing_its_table_with_an_owned_reference_is_refused()
    {
        // The owned columns live on the owner's table but are absent from the owner's own column
        // mappings, so the insert wrote Name and left Address_City and Address_Line1 to whatever
        // the table's defaults gave them.
        using var context = new InteropContext();

        var error = Should.Throw<BulkNotSupportedException>(
            () => Plan<Account>(context, EntityState.Added));

        error.Message.ShouldContain("shares table");
        error.Message.ShouldContain("Address");
        error.Message.ShouldContain("owned by it");
    }

    [Fact]
    public void An_owner_whose_owned_type_is_mapped_to_json_is_refused()
    {
        // Same shape, one column rather than several: the JSON document is a column on the owner's
        // table that the owner does not map itself.
        using var context = new InteropContext();

        var error = Should.Throw<BulkNotSupportedException>(
            () => Plan<JsonOwner>(context, EntityState.Added));

        error.Message.ShouldContain("shares table");
        error.Message.ShouldContain("JSON column");
    }

    [Fact]
    public void An_owner_whose_owned_collection_has_its_own_table_is_allowed()
    {
        // Nothing shares the owner's table here, so its own row is complete and writable. The
        // owned rows are a separate entity type in a separate table — the same position an ordinary
        // dependent is in, which IncludeGraph() exists to cover.
        using var context = new InteropContext();

        Columns(Plan<Basket>(context, EntityState.Added))
            .ShouldBe([nameof(Basket.Id), nameof(Basket.Label)], ignoreOrder: true);
    }

    [Fact]
    public void An_entity_that_shares_no_table_is_allowed()
    {
        using var context = new InteropContext();

        Columns(Plan<Plain>(context, EntityState.Added)).ShouldNotBeEmpty();
    }

    // ---------------------------------------------------------------------------------------
    // Concurrency tokens
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_concurrency_token_is_written_on_an_insert()
    {
        // An insert locates no existing row, so there is nothing to check the token against and
        // nothing lost by writing it like any other column — which is what SaveChanges does too.
        using var context = new InteropContext();

        var plan = Plan<Versioned>(context, EntityState.Added);

        plan.Columns.Single(c => c.Name == nameof(Versioned.Etag)).IsWrite.ShouldBeTrue();
    }

    [Theory]
    [InlineData(EntityState.Modified, "An update")]
    [InlineData(EntityState.Deleted, "A delete")]
    public void A_concurrency_token_is_refused_on_an_operation_that_locates_a_row(
        EntityState state,
        string expected)
    {
        // Without a before-image the token cannot go in the WHERE clause, and continuing without it
        // downgrades an optimistic-concurrency check to last-writer-wins — silently, on a call that
        // otherwise looks like it worked.
        using var context = new InteropContext();

        var error = Should.Throw<BulkNotSupportedException>(() => Plan<Versioned>(context, state));

        error.Message.ShouldContain("concurrency token");
        error.Message.ShouldContain(expected);
        error.Message.ShouldContain("last-writer-wins");
    }

    [Fact]
    public void A_concurrency_token_is_refused_on_a_merge()
    {
        // A merge is state Added plus match columns, and its update arm rewrites rows that already
        // exist — so it loses exactly what an update would.
        using var context = new InteropContext();

        var entityType = context.Model.FindEntityType(typeof(Versioned))!;

        var error = Should.Throw<BulkNotSupportedException>(() => BulkEntityPlan.For(
            entityType,
            EntityState.Added,
            [entityType.FindProperty(nameof(Versioned.Code))!],
            projection: null));

        error.Message.ShouldContain("A merge or synchronise");
    }

    [Fact]
    public void A_store_generated_row_version_is_refused_on_an_update()
    {
        // A rowversion is a concurrency token the database maintains. It is read back after an
        // insert, which is fine, but on an update it is still the column that decides whether the
        // row changed underneath the caller.
        using var context = new InteropContext();

        Should.Throw<BulkNotSupportedException>(() => Plan<RowVersioned>(context, EntityState.Modified))
            .Message.ShouldContain("concurrency token");
    }

    [Fact]
    public void An_entity_without_a_token_is_updated_and_deleted_normally()
    {
        using var context = new InteropContext();

        Plan<Plain>(context, EntityState.Modified).Columns.ShouldContain(c => c.IsWrite);
        Plan<Plain>(context, EntityState.Deleted).Columns.ShouldContain(c => c.IsCondition);
    }

    // ---------------------------------------------------------------------------------------
    // Shadow properties and value converters
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_shadow_property_is_refused()
    {
        using var context = new InteropContext();

        Should.Throw<BulkNotSupportedException>(() => Plan<Shadowed>(context, EntityState.Added))
            .Message.ShouldContain("shadow property");
    }

    [Fact]
    public void A_value_converted_property_is_planned_with_its_store_mapping()
    {
        // The bulk writers push values at the provider directly rather than going through EF's
        // parameter construction, so the converter has to come along on the column.
        using var context = new InteropContext();

        var plan = Plan<Converted>(context, EntityState.Added);
        var column = plan.Columns.Single(c => c.Name == nameof(Converted.Status));

        column.IsWrite.ShouldBeTrue();

        // Asserted through the conversion the writers actually perform, rather than on the
        // converter's presence: what matters is that the enum reaches the provider as its text and
        // comes back as the enum, in both directions.
        column.ProviderClrType.ShouldBe(typeof(string));
        column.ToProviderValue(OrderStatus.Shipped).ShouldBe(nameof(OrderStatus.Shipped));
        column.FromProviderValue(nameof(OrderStatus.Shipped)).ShouldBe(OrderStatus.Shipped);
    }

    // ---------------------------------------------------------------------------------------
    // Complex types
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_complex_property_contributes_its_columns()
    {
        // A complex type's columns are on the entity's table but mapped by the complex type, so
        // reading the entity's own column mappings wrote everything else and left these to the
        // table's defaults — an insert that reported success and lost half a value object.
        using var context = new InteropContext();

        Columns(Plan<Invoice>(context, EntityState.Added)).ShouldBe(
            [
                nameof(Invoice.Id),
                nameof(Invoice.Reference),
                "Money_Amount",
                "Money_Currency",
                "Money_Stamp_By",
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void A_complex_property_is_read_through_the_members_that_hold_it()
    {
        // Including one nested inside another: the accessor has to walk Money then Stamp, not cast
        // the entity to whichever type happens to declare the property.
        using var context = new InteropContext();

        var plan = Plan<Invoice>(context, EntityState.Added);
        var invoice = new Invoice
        {
            Id = 1,
            Reference = "INV-1",
            Money = new Money
            {
                Amount = 12.5m,
                Currency = "GBP",
                Stamp = new Stamp { By = "ada" },
            },
        };

        Read(plan, "Money_Amount", invoice).ShouldBe(12.5m);
        Read(plan, "Money_Currency", invoice).ShouldBe("GBP");
        Read(plan, "Money_Stamp_By", invoice).ShouldBe("ada");
    }

    [Fact]
    public void An_absent_optional_complex_value_reads_as_nulls()
    {
        // EF writes null to every column of an absent optional complex value. Reading it has to do
        // the same rather than dereference it, which would throw partway through streaming a batch.
        using var context = new InteropContext();

        var plan = Plan<Quote>(context, EntityState.Added);
        var quote = new Quote { Id = 1, Money = null };

        Read(plan, "Money_Amount", quote).ShouldBeNull();
        Read(plan, "Money_Currency", quote).ShouldBeNull();
        Read(plan, "Money_Stamp_By", quote).ShouldBeNull();
    }

    [Fact]
    public void A_present_optional_complex_value_reads_normally()
    {
        using var context = new InteropContext();

        var plan = Plan<Quote>(context, EntityState.Added);
        var quote = new Quote { Id = 1, Money = new Money { Amount = 3m, Currency = "EUR" } };

        Read(plan, "Money_Amount", quote).ShouldBe(3m);
        Read(plan, "Money_Currency", quote).ShouldBe("EUR");
    }

    [Fact]
    public void A_complex_property_is_written_on_an_update_and_is_not_a_row_locator()
    {
        using var context = new InteropContext();

        var plan = Plan<Invoice>(context, EntityState.Modified);
        var amount = plan.Columns.Single(c => c.Name == "Money_Amount");

        amount.IsWrite.ShouldBeTrue();
        amount.IsCondition.ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------------------
    // Keys
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void An_alternate_key_is_an_ordinary_writable_column()
    {
        // IProperty.IsKey() is true for an alternate key too. Treating one as a row locator put it
        // in the WHERE clause and kept it out of the SET clause, so an update whose alternate key
        // had changed — the usual reason to run one — matched no row and reported no error.
        using var context = new InteropContext();

        var plan = Plan<Catalogued>(context, EntityState.Modified);
        var code = plan.Columns.Single(c => c.Name == nameof(Catalogued.Code));

        code.IsWrite.ShouldBeTrue();
        code.IsCondition.ShouldBeFalse();
        code.IsKey.ShouldBeFalse();
    }

    [Fact]
    public void Only_the_primary_key_locates_the_row()
    {
        using var context = new InteropContext();

        var conditions = Plan<Catalogued>(context, EntityState.Deleted)
            .Columns.Where(c => c.IsCondition)
            .Select(c => c.Name);

        conditions.ShouldBe([nameof(Catalogued.Id)]);
    }

    [Fact]
    public void A_composite_primary_key_locates_the_row_by_all_of_its_columns()
    {
        using var context = new InteropContext();

        var conditions = Plan<Membership>(context, EntityState.Deleted)
            .Columns.Where(c => c.IsCondition)
            .Select(c => c.Name);

        conditions.ShouldBe([nameof(Membership.GroupId), nameof(Membership.UserId)], ignoreOrder: true);
    }

    [Theory]
    [InlineData(EntityState.Modified, "an update")]
    [InlineData(EntityState.Deleted, "a delete")]
    public void A_keyless_entity_type_is_refused_for_anything_that_locates_a_row(
        EntityState state,
        string expected)
    {
        // The worst outcome the planner could produce, and it did: no key means no condition
        // columns, so the update went out with an empty WHERE clause and rewrote the whole table.
        using var context = new InteropContext();

        var error = Should.Throw<BulkNotSupportedException>(() => Plan<Reading>(context, state));

        error.Message.ShouldContain("no primary key");
        error.Message.ShouldContain(expected);
        error.Message.ShouldContain("every row");
    }

    [Fact]
    public void A_keyless_entity_type_can_still_be_inserted()
    {
        // An insert locates no row, which is exactly why the missing key costs it nothing.
        using var context = new InteropContext();

        Columns(Plan<Reading>(context, EntityState.Added))
            .ShouldBe([nameof(Reading.Label), nameof(Reading.Value)], ignoreOrder: true);
    }

    [Fact]
    public void A_keyless_entity_type_with_explicit_match_columns_is_allowed()
    {
        // The refusal is about there being no locator at all, not about the key specifically. A
        // caller who names one has answered the question the missing key left open.
        using var context = new InteropContext();

        var entityType = context.Model.FindEntityType(typeof(Reading))!;

        var plan = BulkEntityPlan.For(
            entityType,
            EntityState.Deleted,
            [entityType.FindProperty(nameof(Reading.Label))!],
            projection: null);

        plan.Columns.Single(c => c.IsCondition).Name.ShouldBe(nameof(Reading.Label));
    }

    // ---------------------------------------------------------------------------------------
    // Types with no CLR members to read
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_property_bag_entity_type_is_refused()
    {
        // The implicit join entity behind a skip navigation. It used to reach the accessor compiler
        // and fail with an ArgumentException about get_Item — accurate, and useless to the caller.
        using var context = new InteropContext();

        var join = context.Model.GetEntityTypes().Single(e => e.IsPropertyBag);

        var error = Should.Throw<BulkNotSupportedException>(
            () => BulkEntityPlan.For(join, EntityState.Added));

        error.Message.ShouldContain("property-bag entity type");
        error.Message.ShouldContain("many-to-many");
    }

    [Fact]
    public void A_temporal_table_is_refused_by_name_rather_than_as_a_shadow_property()
    {
        // The period columns are shadow properties nobody declared, so the generic message sent
        // people looking for something they had not written.
        using var context = new TemporalContext();

        var error = Should.Throw<BulkNotSupportedException>(
            () => BulkEntityPlan.For(
                context.Model.FindEntityType(typeof(Ledger))!, EntityState.Added));

        error.Message.ShouldContain("temporal table");
        error.Message.ShouldContain("period columns");
    }

    // ---------------------------------------------------------------------------------------
    // Mappings that need nothing special
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_table_per_concrete_type_entity_is_allowed()
    {
        // TPC gives every concrete type a table of its own carrying every column it has, inherited
        // ones included — so unlike TPT there is exactly one table to write and nothing is missing.
        using var context = new InteropContext();

        Columns(Plan<TpcCat>(context, EntityState.Added)).ShouldBe(
            [nameof(TpcAnimal.Id), nameof(TpcAnimal.Name), nameof(TpcCat.Lives)],
            ignoreOrder: true);
    }

    [Fact]
    public void A_primitive_collection_is_one_ordinary_column()
    {
        // EF 8 maps a collection of primitives to a single JSON column on the entity's own table,
        // so it needs nothing beyond the value conversion the column already carries.
        using var context = new InteropContext();

        var plan = Plan<Article>(context, EntityState.Added);

        plan.Columns.Select(c => c.Name).ShouldContain(nameof(Article.Tags));
        plan.Columns.Single(c => c.Name == nameof(Article.Tags)).IsWrite.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Global query filters
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unscoped_synchronize_under_a_query_filter_is_refused()
    {
        // The delete arm removes every row the source does not name. Under a filter that set
        // includes the rows the filter exists to hide, which this context cannot even read.
        await using var context = InteropContext.WithBulk();

        var error = await Should.ThrowAsync<BulkNotSupportedException>(
            () => context.BulkSynchronizeAsync(
                [new Tenanted { Id = 1, TenantId = "t1" }],
                o => o.AllowFullTableDelete()));

        error.Message.ShouldContain("global query filter");
        error.Message.ShouldContain("WithinScope");
        error.Message.ShouldContain("AllowFullTableDelete() does not stand in for it");
    }

    [Fact]
    public async Task A_named_query_filter_is_named_in_the_refusal()
    {
        await using var context = InteropContext.WithBulk();

        var error = await Should.ThrowAsync<BulkNotSupportedException>(
            () => context.BulkSynchronizeAsync(
                [new NamedFiltered { Id = 1, TenantId = "t1" }],
                o => o.AllowFullTableDelete()));

        error.Message.ShouldContain("'tenant'");
    }

    [Fact]
    public async Task A_scoped_synchronize_under_a_query_filter_is_allowed_through()
    {
        // A scope is the caller stating which rows the synchronise owns, so it settles the question
        // the filter raised. It has to get past the guard and on to execution — which is as far as
        // this test can follow it, because the connection string does not connect.
        await using var context = InteropContext.WithBulk();

        var error = await Should.ThrowAsync<Exception>(
            () => context.BulkSynchronizeAsync(
                [new Tenanted { Id = 1, TenantId = "t1" }],
                o => o.WithinScope(e => e.TenantId == "t1")));

        error.ShouldNotBeOfType<BulkNotSupportedException>();
    }

    [Fact]
    public async Task A_synchronize_without_a_query_filter_is_unaffected()
    {
        // The guard is about filters, not about synchronising, so an unfiltered type reaches
        // execution on the terms it always did.
        await using var context = InteropContext.WithBulk();

        var error = await Should.ThrowAsync<Exception>(
            () => context.BulkSynchronizeAsync(
                [new Plain { Id = 1, Name = "a" }],
                o => o.AllowFullTableDelete()));

        error.ShouldNotBeOfType<BulkNotSupportedException>();
    }

    [Fact]
    public void An_update_or_delete_under_a_query_filter_is_not_refused()
    {
        // Deliberately not guarded. These reach only the rows the caller handed over, located by
        // key — the same rows SaveChanges would write, where stock EF applies no query filter
        // either. A synchronise is the one verb that reaches rows nobody named.
        using var context = new InteropContext();

        Plan<Tenanted>(context, EntityState.Modified).Columns.ShouldNotBeEmpty();
        Plan<Tenanted>(context, EntityState.Deleted).Columns.ShouldNotBeEmpty();
    }

    private static BulkEntityPlan Plan<TEntity>(InteropContext context, EntityState state)
        => BulkEntityPlan.For(context.Model.FindEntityType(typeof(TEntity))!, state);

    private static string[] Columns(BulkEntityPlan plan)
        => [.. plan.Columns.Select(c => c.Name)];

    /// <summary>Reads one column's value off <paramref name="entity" /> through the plan's accessor.</summary>
    private static object? Read(BulkEntityPlan plan, string column, object entity)
    {
        for (var i = 0; i < plan.Columns.Count; i++)
        {
            if (plan.Columns[i].Name == column)
            {
                return plan.Getters[i](entity);
            }
        }

        throw new InvalidOperationException($"'{column}' is not in the plan.");
    }

    private sealed class InteropContext : DbContext
    {
        public InteropContext()
        {
        }

        private InteropContext(DbContextOptions<InteropContext> options) : base(options)
        {
        }

        /// <summary>A context with the bulk services registered, for the calls that resolve them.</summary>
        public static InteropContext WithBulk()
            => new(new DbContextOptionsBuilder<InteropContext>()
                .UseSqlServer("Server=none;Database=none;Connect Timeout=1")
                .UseSqlServerBulk()
                .Options);

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Never opened by anything that only builds a plan. The two synchronise tests that do
            // reach execution assert on which exception they get, not on success.
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlServer("Server=none;Database=none;Connect Timeout=1").UseSqlServerBulk();
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TptPerson>().ToTable("TptPeople");
            modelBuilder.Entity<TptEmployee>().ToTable("TptEmployees");

            modelBuilder.Entity<TphCard>()
                .HasDiscriminator(c => c.Kind)
                .HasValue<TphCard>("card")
                .HasValue<TphCreditCard>("credit");

            modelBuilder.Entity<ShadowDiscriminated>()
                .HasDiscriminator<string>("Kind")
                .HasValue<ShadowDiscriminated>("base")
                .HasValue<ShadowDerived>("derived");

            modelBuilder.Entity<SplitDocument>()
                .SplitToTable("SplitDocumentDetails", t => t.Property(d => d.Body));

            modelBuilder.Entity<Account>().OwnsOne(a => a.Address);
            modelBuilder.Entity<JsonOwner>().OwnsOne(o => o.Payload, p => p.ToJson());
            modelBuilder.Entity<Basket>().OwnsMany(b => b.Items, i => i.ToTable("BasketItems"));

            modelBuilder.Entity<Versioned>().Property(v => v.Etag).IsConcurrencyToken();
            modelBuilder.Entity<RowVersioned>().Property(v => v.Version).IsRowVersion();

            modelBuilder.Entity<Shadowed>().Property<string>("Secret");
            modelBuilder.Entity<Converted>().Property(c => c.Status).HasConversion<string>();

            modelBuilder.Entity<Tenanted>().HasQueryFilter(t => t.TenantId == "t1");
            modelBuilder.Entity<NamedFiltered>().HasQueryFilter("tenant", t => t.TenantId == "t1");

            modelBuilder.Entity<Invoice>()
                .ComplexProperty(i => i.Money, m => m.ComplexProperty(x => x.Stamp));

            modelBuilder.Entity<Quote>().ComplexProperty(q => q.Money);

            modelBuilder.Entity<Catalogued>().HasAlternateKey(c => c.Code);
            modelBuilder.Entity<Membership>().HasKey(m => new { m.GroupId, m.UserId });
            modelBuilder.Entity<Reading>().HasNoKey().ToTable("Readings");

            modelBuilder.Entity<TpcAnimal>().UseTpcMappingStrategy().ToTable("TpcAnimals");
            modelBuilder.Entity<TpcCat>().ToTable("TpcCats");

            modelBuilder.Entity<Article>();
            modelBuilder.Entity<Post>().HasMany(p => p.Labels).WithMany(l => l.Posts);

            modelBuilder.Entity<Plain>();
        }
    }

    private sealed class TemporalContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlServer("Server=none;Database=none;Connect Timeout=1");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Ledger>().ToTable("Ledgers", t => t.IsTemporal());
    }

    public class TptPerson
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class TptEmployee : TptPerson
    {
        public string? Title { get; set; }
    }

    public class TphCard
    {
        public int Id { get; set; }
        public string Kind { get; set; } = string.Empty;
    }

    public sealed class TphCreditCard : TphCard
    {
        public decimal Limit { get; set; }
    }

    public class ShadowDiscriminated
    {
        public int Id { get; set; }
    }

    public sealed class ShadowDerived : ShadowDiscriminated
    {
        public string? Extra { get; set; }
    }

    public sealed class SplitDocument
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }

    public sealed class Account
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Address Address { get; set; } = new();
    }

    public sealed class Address
    {
        public string Line1 { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
    }

    public sealed class JsonOwner
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Payload Payload { get; set; } = new();
    }

    public sealed class Payload
    {
        public string Note { get; set; } = string.Empty;
    }

    public sealed class Basket
    {
        public int Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public List<BasketItem> Items { get; } = [];
    }

    public sealed class BasketItem
    {
        public string Sku { get; set; } = string.Empty;
    }

    public sealed class Versioned
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Etag { get; set; } = string.Empty;
    }

    public sealed class RowVersioned
    {
        public int Id { get; set; }
        public byte[] Version { get; set; } = [];
    }

    public sealed class Shadowed
    {
        public int Id { get; set; }
    }

    public sealed class Converted
    {
        public int Id { get; set; }
        public OrderStatus Status { get; set; }
    }

    public enum OrderStatus
    {
        Placed,
        Shipped,
    }

    public sealed class Tenanted
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = string.Empty;
    }

    public sealed class NamedFiltered
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = string.Empty;
    }

    public sealed class Plain
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class Invoice
    {
        public int Id { get; set; }
        public string Reference { get; set; } = string.Empty;
        public Money Money { get; set; } = new();
    }

    /// <summary>Carries a nested complex value, so the accessor has to walk more than one member.</summary>
    public sealed class Money
    {
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public Stamp Stamp { get; set; } = new();
    }

    public sealed class Stamp
    {
        public string By { get; set; } = string.Empty;
    }

    /// <summary>An optional complex value, which EF writes as nulls when it is absent.</summary>
    public sealed class Quote
    {
        public int Id { get; set; }
        public Money? Money { get; set; }
    }

    public sealed class Catalogued
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    public sealed class Membership
    {
        public string GroupId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }

    public sealed class Reading
    {
        public string Label { get; set; } = string.Empty;
        public int Value { get; set; }
    }

    public class TpcAnimal
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class TpcCat : TpcAnimal
    {
        public int Lives { get; set; }
    }

    public sealed class Article
    {
        public int Id { get; set; }
        public List<string> Tags { get; set; } = [];
    }

    public sealed class Post
    {
        public int Id { get; set; }
        public List<Label> Labels { get; } = [];
    }

    public sealed class Label
    {
        public int Id { get; set; }
        public List<Post> Posts { get; } = [];
    }

    public sealed class Ledger
    {
        public int Id { get; set; }
        public string Entry { get; set; } = string.Empty;
    }
}
