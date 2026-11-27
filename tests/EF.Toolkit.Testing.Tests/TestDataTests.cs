using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing.Tests;

public sealed class TestDataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Required_scalars_are_filled_and_optional_ones_left_null()
    {
        using var context = Contexts.Sqlite();

        var customer = new TestData().Build<Customer>(context);

        customer.Name.ShouldNotBeNullOrEmpty();
        customer.Email.ShouldNotBeNullOrEmpty();
        customer.ExternalId.ShouldNotBe(Guid.Empty);
        customer.CreatedAt.ShouldNotBe(default);
        customer.CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        customer.Nickname.ShouldBeNull();
        customer.LoyaltyPoints.ShouldBeNull();
        Enum.IsDefined(customer.Tier).ShouldBeTrue();
    }

    [Fact]
    public void Optional_scalars_are_filled_when_asked()
    {
        using var context = Contexts.Sqlite();

        var customer = new TestData().Build<Customer>(context, fillOptional: true);

        customer.Nickname.ShouldNotBeNull();
        customer.LoyaltyPoints.ShouldNotBeNull();
    }

    [Fact]
    public void Store_generated_properties_are_left_for_the_database()
    {
        using var context = Contexts.Sqlite();

        var customer = new TestData().Build<Customer>(context);

        customer.Id.ShouldBe(0);
        customer.RowStamp.ShouldBeNull();
    }

    [Fact]
    public void A_client_supplied_key_is_generated()
    {
        using var context = Contexts.Sqlite();
        var data = new TestData();

        var first = data.Build<Tag>(context);
        var second = data.Build<Tag>(context);

        first.Code.ShouldNotBeNullOrEmpty();
        first.Code.ShouldNotBe(second.Code);
    }

    [Fact]
    public void Strings_respect_their_maximum_length_and_stay_unique()
    {
        using var context = Contexts.Sqlite();
        var data = new TestData();

        var customers = data.BuildMany<Customer>(context, 500);
        var products = data.BuildMany<Product>(context, 500);

        customers.ShouldAllBe(c => c.Name.Length <= 12);
        customers.Select(c => c.Name).Distinct().Count().ShouldBe(500);
        customers.Select(c => c.Email).Distinct().Count().ShouldBe(500);
        products.ShouldAllBe(p => p.Sku.Length <= 4);
        products.Select(p => p.Sku).Distinct().Count().ShouldBe(500);
    }

    [Fact]
    public void A_string_that_fits_keeps_the_property_name()
    {
        using var context = Contexts.Sqlite();

        new TestData().Build<Customer>(context).Email.ShouldStartWith("Email-");
    }

    [Fact]
    public void Decimals_stay_inside_their_precision()
    {
        using var context = Contexts.Sqlite();

        // decimal(5,2) holds at most 999.99.
        new TestData().BuildMany<Customer>(context, 2_000).ShouldAllBe(c => c.Balance < 1_000m);
    }

    [Fact]
    public void Byte_arrays_respect_their_maximum_length()
    {
        using var context = Contexts.Sqlite();

        new TestData().Build<Product>(context).Thumbprint.Length.ShouldBe(3);
    }

    [Fact]
    public void Primitive_collections_get_one_element()
    {
        using var context = Contexts.Sqlite();

        new TestData().Build<Product>(context).Tags.ShouldHaveSingleItem().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void A_converted_enum_still_gets_a_defined_value()
    {
        using var context = Contexts.Sqlite();

        var tiers = new TestData().BuildMany<Customer>(context, 100).Select(c => c.Tier).ToList();

        tiers.ShouldAllBe(t => Enum.IsDefined(t));
        tiers.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void Complex_properties_are_filled_recursively()
    {
        using var context = Contexts.Sqlite();

        var customer = new TestData().Build<Customer>(context);

        customer.Address.ShouldNotBeNull();
        customer.Address.Street.ShouldNotBeNullOrEmpty();
        customer.Address.City.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Required_owned_references_are_built()
    {
        using var context = Contexts.Sqlite();

        var order = new TestData().Build<Order>(context);

        order.Shipping.ShouldNotBeNull();
        order.Shipping.Carrier.ShouldNotBeNullOrEmpty();
        order.Shipping.Tracking.ShouldBeNull();
    }

    [Fact]
    public void Required_principals_are_built_recursively_and_never_shared()
    {
        using var context = Contexts.Sqlite();
        var data = new TestData();

        var line = data.Build<OrderLine>(context);
        var other = data.Build<OrderLine>(context);

        line.Order.ShouldNotBeNull();
        line.Order.Customer.ShouldNotBeNull();
        line.Product.ShouldNotBeNull();
        line.OrderId.ShouldBe(0);
        other.Order.ShouldNotBeSameAs(line.Order);
        other.Order.Customer.ShouldNotBeSameAs(line.Order.Customer);
    }

    [Fact]
    public void Optional_references_are_not_built()
    {
        using var context = Contexts.Sqlite();

        new TestData().Build<Category>(context).Parent.ShouldBeNull();
        new TestData().Build<Team>(context).Captain.ShouldBeNull();
    }

    [Fact]
    public void Principals_stop_at_the_depth_limit()
    {
        using var context = Contexts.Sqlite();

        new TestData(maxDepth: 0).Build<OrderLine>(context).Order.ShouldBeNull();

        var line = new TestData(maxDepth: 1).Build<OrderLine>(context);
        line.Order.ShouldNotBeNull();
        line.Order.Customer.ShouldBeNull();
    }

    [Fact]
    public void Customize_runs_last()
    {
        using var context = Contexts.Sqlite();

        var customer = new TestData().Build<Customer>(context, c => c.Name = "Alice");

        customer.Name.ShouldBe("Alice");
    }

    [Fact]
    public void Setting_the_foreign_key_drops_the_generated_principal()
    {
        using var context = Contexts.Sqlite();

        var order = new TestData().Build<Order>(context, o => o.CustomerId = 42);

        order.Customer.ShouldBeNull();
        order.CustomerId.ShouldBe(42);
    }

    [Fact]
    public void A_principal_the_test_attaches_is_kept()
    {
        using var context = Contexts.Sqlite();
        var alice = ShopData.Customer("Alice");

        var order = new TestData().Build<Order>(context, o =>
        {
            o.Customer = alice;
            o.CustomerId = 7;
        });

        order.Customer.ShouldBeSameAs(alice);
    }

    [Fact]
    public void The_same_seed_builds_the_same_values()
    {
        using var context = Contexts.Sqlite();

        var first = new TestData(seed: 7).BuildMany<Customer>(context, 3);
        var second = new TestData(seed: 7).BuildMany<Customer>(context, 3);

        first.Select(Snapshot).ShouldBe(second.Select(Snapshot));
    }

    [Fact]
    public void A_different_seed_builds_different_guids()
    {
        using var context = Contexts.Sqlite();

        new TestData(seed: 1).Build<Customer>(context).ExternalId
            .ShouldNotBe(new TestData(seed: 2).Build<Customer>(context).ExternalId);
    }

    [Fact]
    public void Unsupported_types_are_left_as_constructed()
    {
        using var context = new GadgetContext(new DbContextOptionsBuilder<GadgetContext>().UseSqlite("Data Source=:memory:").Options);

        var gadget = new TestData().Build<Gadget>(context);

        gadget.Serial.ShouldBeNull();
        gadget.Label.ShouldNotBeNullOrEmpty();
        gadget.Kind.ShouldBe(default);
    }

    [Fact]
    public void Every_supported_type_gets_a_value()
    {
        using var context = new AllTypesContext(new DbContextOptionsBuilder<AllTypesContext>().UseSqlite("Data Source=:memory:").Options);
        var data = new TestData();

        var first = data.Build<AllTypes>(context);
        var second = data.Build<AllTypes>(context);

        first.IntValue.ShouldNotBe(second.IntValue);
        first.LongValue.ShouldNotBe(second.LongValue);
        first.ShortValue.ShouldNotBe(second.ShortValue);
        first.ByteValue.ShouldNotBe(second.ByteValue);
        first.DoubleValue.ShouldNotBe(second.DoubleValue);
        first.FloatValue.ShouldNotBe(second.FloatValue);
        first.DecimalValue.ShouldNotBe(second.DecimalValue);
        first.CharValue.ShouldNotBe(second.CharValue);
        data.BuildMany<AllTypes>(context, 50).Select(a => a.BoolValue).Distinct().Count().ShouldBe(2);
        first.GuidValue.ShouldNotBe(second.GuidValue);
        first.DateTimeValue.ShouldNotBe(second.DateTimeValue);
        first.DateTimeOffsetValue.ShouldNotBe(second.DateTimeOffsetValue);
        first.DateTimeOffsetValue.Offset.ShouldBe(TimeSpan.Zero);
        first.DateOnlyValue.ShouldNotBe(second.DateOnlyValue);
        first.TimeOnlyValue.ShouldNotBe(second.TimeOnlyValue);
        first.TimeSpanValue.ShouldNotBe(second.TimeSpanValue);
        first.UIntValue.ShouldNotBe(second.UIntValue);
        first.ULongValue.ShouldNotBe(second.ULongValue);
        first.UShortValue.ShouldNotBe(second.UShortValue);
        first.SByteValue.ShouldNotBe(second.SByteValue);
        first.BytesValue.ShouldNotBe(second.BytesValue);
        first.ArrayValue.ShouldHaveSingleItem();
        first.Readonly.ShouldNotBeNullOrEmpty();
        first.Point.X.ShouldNotBe(0);
    }

    [Fact]
    public async Task Built_entities_insert_cleanly_with_their_principals()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();

        var line = await context.CreateAsync<OrderLine>(cancellationToken: Ct);

        line.Id.ShouldBeGreaterThan(0);
        line.Order.Id.ShouldBeGreaterThan(0);
        line.Order.Customer.Id.ShouldBeGreaterThan(0);
        context.ChangeTracker.Entries().ShouldBeEmpty();
        (await context.Customers.CountAsync(Ct)).ShouldBe(1);
        (await context.Customers.SingleAsync(Ct)).RowStamp.ShouldBe("generated");
    }

    [Fact]
    public async Task Many_entities_insert_without_tripping_unique_indexes()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();

        var customers = await context.CreateManyAsync<Customer>(50, (c, i) => c.Nickname = $"n{i}", cancellationToken: Ct);
        await context.CreateManyAsync<Product>(50, cancellationToken: Ct);

        customers.Select(c => c.Nickname).ShouldBe(Enumerable.Range(0, 50).Select(i => $"n{i}"));
        (await context.Customers.CountAsync(Ct)).ShouldBe(50);
    }

    [Fact]
    public async Task A_foreign_key_set_in_customize_points_at_the_chosen_principal()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        var alice = await context.CreateAsync<Customer>(c => c.Name = "Alice", cancellationToken: Ct);

        await context.CreateAsync<Order>(o => o.CustomerId = alice.Id, cancellationToken: Ct);

        (await context.Customers.CountAsync(Ct)).ShouldBe(1);
        (await context.Orders.Include(o => o.Customer).SingleAsync(Ct)).Customer.Name.ShouldBe("Alice");
    }

    [Fact]
    public async Task A_context_from_a_test_database_uses_that_database_builder()
    {
        await using var one = await SqliteTestDatabase<ShopContext>.CreateAsync(o => o.WithTestDataSeed(3), Ct);
        await using var two = await SqliteTestDatabase<ShopContext>.CreateAsync(o => o.WithTestDataSeed(3), Ct);
        await using var first = one.CreateContext();
        await using var second = two.CreateContext();

        one.Data.Seed.ShouldBe(3);
        Snapshot(first.Build<Customer>()).ShouldBe(Snapshot(second.Build<Customer>()));
        first.BuildMany<Customer>(2).Select(Snapshot).ShouldBe(second.BuildMany<Customer>(2).Select(Snapshot));
    }

    private sealed class DetailedShopContext(DbContextOptions<ShopContext> options) : ShopContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.EnableDetailedErrors();
    }

    [Fact]
    public async Task A_context_that_adds_an_option_in_OnConfiguring_keeps_the_database_builder()
    {
        // OnConfiguring gives the context options of its own, which the database's registration never saw:
        // it fell back to the process-wide builder, and two databases with one seed built different values.
        await using var one = await SqliteTestDatabase<ShopContext>.CreateAsync(
            o => o.WithTestDataSeed(5).UseContextFactory(options => new DetailedShopContext(options)), Ct);
        await using var context = one.CreateContext();

        Snapshot(context.Build<Customer>()).ShouldBe(Snapshot(new TestData(5).Build<Customer>(context)));
    }

    private sealed class Discount
    {
        public int Id { get; set; }
        public decimal Rate { get; set; }
        public string Code { get; set; } = "";
        public byte Level { get; set; }
    }

    private sealed class DiscountContext(DbContextOptions<DiscountContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Discount>(b =>
            {
                b.Property(d => d.Rate).HasPrecision(2, 2);
                b.HasIndex(d => d.Rate).IsUnique();
                b.HasIndex(d => d.Level).IsUnique();
            });
    }

    private sealed class Badge
    {
        public int Id { get; set; }
        public string X { get; set; } = "";
        public string Typed { get; set; } = "";
        public byte[] Blob { get; set; } = [];
    }

    private sealed class BadgeContext(DbContextOptions<BadgeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Badge>(b =>
            {
                b.Property(x => x.X).HasMaxLength(4);
                b.HasIndex(x => x.X).IsUnique();

                // Lengths only in the column type: no max-length facet, just the resolved mapping's Size.
                b.Property(x => x.Typed).HasColumnType("varchar(4)");
                b.Property(x => x.Blob).HasColumnType("varbinary(3)");
            });
    }

    [Fact]
    public async Task A_short_unique_string_never_repeats_while_its_column_still_has_room()
    {
        // X-30 came out for counter 30 in decimal and for counter 108 in base 36.
        await using var db = await SqliteTestDatabase<BadgeContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();

        var badges = await context.CreateManyAsync<Badge>(108, cancellationToken: Ct);

        badges.Select(b => b.X).Distinct().Count().ShouldBe(108);
        badges.ShouldAllBe(b => b.X.Length <= 4);
    }

    [Fact]
    public void Lengths_declared_only_in_the_column_type_are_kept_to()
    {
        // GetMaxLength() is null for HasColumnType("varchar(4)"); Typed-1 overflowed it.
        using var context = new BadgeContext(new DbContextOptionsBuilder<BadgeContext>().UseSqlServer("Server=.;Database=unused").Options);

        var badge = context.Build<Badge>();

        badge.Typed.Length.ShouldBeLessThanOrEqualTo(4);
        badge.Blob.Length.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task A_decimal_with_no_integer_digits_still_gets_distinct_values_that_fit()
    {
        // decimal(2,2) has no room before the point, and every generated value was 0: the second insert
        // broke the unique index.
        await using var db = await SqliteTestDatabase<DiscountContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();

        var discounts = await context.CreateManyAsync<Discount>(5, cancellationToken: Ct);

        discounts.Select(d => d.Rate).Distinct().Count().ShouldBe(5);
        discounts.ShouldAllBe(d => d.Rate > -1m && d.Rate < 1m && decimal.Round(d.Rate, 2) == d.Rate);
    }

    [Fact]
    public async Task A_bounded_column_gets_every_value_it_holds_before_one_repeats()
    {
        // One counter ran across Code, Rate and Level, so each row moved it three: Rate repeated at row 35
        // of the 100 decimal(2,2) holds, and Level, a byte, at row 86 of 255.
        await using var db = await SqliteTestDatabase<DiscountContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();

        var discounts = await context.CreateManyAsync<Discount>(100, cancellationToken: Ct);

        discounts.Select(d => d.Rate).Distinct().Count().ShouldBe(100);
        discounts.Select(d => d.Level).Distinct().Count().ShouldBe(100);
    }

    [Fact]
    public async Task A_context_from_an_isolated_scope_uses_the_database_builder_too()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(o => o.WithTestDataSeed(9), Ct);
        await using var scope = await db.BeginIsolatedScopeAsync(cancellationToken: Ct);
        var context = scope.CreateContext();

        var built = context.Build<Customer>();

        Snapshot(built).ShouldBe(Snapshot(new TestData(9).Build<Customer>(context)));
    }

    [Fact]
    public void Any_other_context_uses_the_shared_builder()
    {
        using var context = Contexts.Sqlite();

        TestData.For(context).ShouldBeSameAs(TestData.Shared);
        context.Build<Tag>().Code.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Unmapped_and_abstract_types_are_refused()
    {
        using var context = Contexts.Sqlite();
        using var animals = new AnimalContext(new DbContextOptionsBuilder<AnimalContext>().UseSqlite("Data Source=:memory:").Options);

        Should.Throw<InvalidOperationException>(() => new TestData().Build<Uri>(context)).Message.ShouldContain("'Uri'");
        Should.Throw<InvalidOperationException>(() => new TestData().Build<Animal>(animals)).Message.ShouldContain("abstract");
    }

    [Fact]
    public void A_required_reference_to_an_abstract_principal_builds_a_concrete_one()
    {
        using var animals = new AnimalContext(new DbContextOptionsBuilder<AnimalContext>().UseSqlite("Data Source=:memory:").Options);

        new TestData().Build<Collar>(animals).Animal.ShouldBeOfType<Dog>();
    }

    [Fact]
    public void Invalid_arguments_are_refused()
    {
        using var context = Contexts.Sqlite();

        Should.Throw<ArgumentOutOfRangeException>(() => new TestData(maxDepth: -1));
        Should.Throw<ArgumentOutOfRangeException>(() => new TestData().BuildMany<Tag>(context, -1));
        Should.Throw<ArgumentNullException>(() => new TestData().Build<Tag>(null!));
        Should.Throw<ArgumentNullException>(() => DbContextTestDataExtensions.Build<Tag>(null!));
        Should.Throw<ArgumentNullException>(() => DbContextTestDataExtensions.BuildMany<Tag>(null!, 1));
        Should.Throw<ArgumentNullException>(() => DbContextTestDataExtensions.CreateAsync<Tag>(null!));
        Should.Throw<ArgumentNullException>(() => DbContextTestDataExtensions.CreateManyAsync<Tag>(null!, 1));
    }

    private static string Snapshot(Customer c)
        => $"{c.Name}|{c.Email}|{c.Tier}|{c.CreatedAt:O}|{c.Balance}|{c.ExternalId}|{c.IsActive}|{c.Address.Street}|{c.Address.City}";

    public sealed class Serial(string value)
    {
        public string Value { get; } = value;
    }

    public enum Empty
    {
    }

    public sealed class Gadget
    {
        public int Id { get; set; }

        public Serial Serial { get; set; } = null!;

        public string Label { get; set; } = null!;

        public Empty Kind { get; set; }
    }

    public sealed class GadgetContext(DbContextOptions<GadgetContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Gadget>()
                .Property(g => g.Serial)
                .HasConversion(s => s.Value, s => new Serial(s));
    }

    public struct Point
    {
        public int X { get; set; }

        public int Y { get; set; }
    }

    public sealed class AllTypes
    {
        private string _readonly = null!;

        public int Id { get; set; }

        public int IntValue { get; set; }

        public long LongValue { get; set; }

        public short ShortValue { get; set; }

        public byte ByteValue { get; set; }

        public sbyte SByteValue { get; set; }

        public uint UIntValue { get; set; }

        public ulong ULongValue { get; set; }

        public ushort UShortValue { get; set; }

        public double DoubleValue { get; set; }

        public float FloatValue { get; set; }

        public decimal DecimalValue { get; set; }

        public char CharValue { get; set; }

        public bool BoolValue { get; set; }

        public Guid GuidValue { get; set; }

        public DateTime DateTimeValue { get; set; }

        public DateTimeOffset DateTimeOffsetValue { get; set; }

        public DateOnly DateOnlyValue { get; set; }

        public TimeOnly TimeOnlyValue { get; set; }

        public TimeSpan TimeSpanValue { get; set; }

        public byte[] BytesValue { get; set; } = null!;

        public int[] ArrayValue { get; set; } = null!;

        public string Readonly => _readonly;

        public Point Point { get; set; }
    }

    public sealed class AllTypesContext(DbContextOptions<AllTypesContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<AllTypes>(b =>
            {
                b.Property(a => a.Readonly).HasField("_readonly");
                b.ComplexProperty(a => a.Point);
            });
    }

    public abstract class Animal
    {
        public int Id { get; set; }

        public string Name { get; set; } = null!;
    }

    public sealed class Dog : Animal
    {
        public bool GoodBoy { get; set; }
    }

    public sealed class Collar
    {
        public int Id { get; set; }

        public Animal Animal { get; set; } = null!;
    }

    public sealed class AnimalContext(DbContextOptions<AnimalContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>();
            modelBuilder.Entity<Dog>();
            modelBuilder.Entity<Collar>().HasOne(c => c.Animal).WithMany().IsRequired();
        }
    }
}
