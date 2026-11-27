using EFToolkit.Conventions.Configuration;
using EFToolkit.Conventions.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Conventions.Tests;

/// <summary>What the model builders record, and what they refuse.</summary>
public sealed class ConfigurationTests
{
    [Fact]
    public void The_model_records_the_soft_delete_property_and_a_named_filter()
    {
        using var context = Context(_ => { });
        var post = context.Model.FindEntityType(typeof(Post))!;

        ConventionMetadata.FindSoftDelete(post)!.Kind.ShouldBe(SoftDeleteKind.Flag);
        post.FindDeclaredQueryFilter(SoftDeleteFilter.Name).ShouldNotBeNull();
        post.FindDeclaredQueryFilter("Tenant").ShouldNotBeNull();

        var derived = context.Model.FindEntityType(typeof(FeaturedPost))!;
        ConventionMetadata.FindSoftDelete(derived)!.Property.Name.ShouldBe(nameof(Post.IsDeleted));

        var comment = context.Model.FindEntityType(typeof(Comment))!;
        ConventionMetadata.FindSoftDelete(comment)!.Kind.ShouldBe(SoftDeleteKind.Timestamp);

        var author = context.Model.FindEntityType(typeof(Author))!;
        var shadow = ConventionMetadata.FindSoftDelete(author)!.Property;
        shadow.IsShadowProperty().ShouldBeTrue();
        shadow.ClrType.ShouldBe(typeof(bool));
    }

    [Fact]
    public void Timestamps_are_found_on_derived_types_too()
    {
        using var context = Context(_ => { });

        var mapping = ConventionMetadata.FindTimestamps(context.Model.FindEntityType(typeof(FeaturedPost))!)!;
        mapping.CreatedAt!.Name.ShouldBe(nameof(Post.CreatedAt));
        mapping.UpdatedAt!.Name.ShouldBe(nameof(Post.UpdatedAt));

        ConventionMetadata.FindTimestamps(context.Model.FindEntityType(typeof(Comment))!).ShouldBeNull();
        ConventionMetadata.FindSoftDelete(context.Model.FindEntityType(typeof(Note))!).ShouldBeNull();
    }

    [Fact]
    public void Soft_delete_on_a_derived_type_is_refused_by_name()
    {
        var error = Should.Throw<InvalidOperationException>(() => Build(m =>
        {
            m.Entity<Post>();
            m.Entity<FeaturedPost>().HasSoftDelete(p => p.IsDeleted);
        }));

        error.Message.ShouldContain("must be configured on 'Post'");
    }

    [Fact]
    public void A_soft_delete_property_of_the_wrong_type_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => Build(m => m.Entity<Post>().HasSoftDelete(nameof(Post.Title))))
            .Message.ShouldContain("must be a bool");
    }

    [Fact]
    public void A_selector_that_is_not_a_property_is_refused()
    {
        Should.Throw<ArgumentException>(() => Build(m => m.Entity<Post>().HasSoftDelete(p => p.Id > 3)))
            .ParamName.ShouldBe("isDeleted");
    }

    [Fact]
    public void A_timestamp_of_the_wrong_type_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => Build(m => m.Entity<Post>().HasCreatedAt(p => p.Tenant)))
            .Message.ShouldContain("must be a DateTime or DateTimeOffset");
    }

    [Fact]
    public void Every_timestamp_type_maps_now_to_its_own_kind_of_value()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2));

        ConventionMetadata.TimestampValue(typeof(DateTimeOffset), now).ShouldBe(now);
        ConventionMetadata.TimestampValue(typeof(DateTimeOffset?), now).ShouldBe(now);

        var utc = (DateTime)ConventionMetadata.TimestampValue(typeof(DateTime?), now);
        utc.Kind.ShouldBe(DateTimeKind.Utc);
        utc.Hour.ShouldBe(10);
    }

    [Fact]
    public void Calling_UseEntityConventions_twice_amends_the_first_call()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var builder = new DbContextOptionsBuilder()
            .UseEntityConventions(c => c.Clock(clock))
            .UseEntityConventions(c => c.WithoutTimestamps());

        var options = builder.Options.FindExtension<ConventionsOptionsExtension>()!.Options;
        options.Clock.ShouldBeSameAs(clock);
        options.Timestamps.ShouldBeFalse();
        options.SoftDelete.ShouldBeTrue();
        builder.Options.Extensions.OfType<ConventionsOptionsExtension>().Count().ShouldBe(1);
    }

    [Fact]
    public void The_extension_describes_itself_for_logs_and_service_provider_caching()
    {
        var a = new ConventionsOptionsExtension(ConventionsOptions.Default);
        var b = new ConventionsOptionsExtension(ConventionsOptions.Default with { Timestamps = false });

        a.Info.IsDatabaseProvider.ShouldBeFalse();
        a.Info.LogFragment.ShouldContain("softDelete=True");
        a.Info.ShouldUseSameServiceProvider(new ConventionsOptionsExtension().Info).ShouldBeTrue();
        a.Info.ShouldUseSameServiceProvider(b.Info).ShouldBeFalse();

        var debug = new Dictionary<string, string>();
        b.Info.PopulateDebugInfo(debug);
        debug["EF.Toolkit.Conventions:Timestamps"].ShouldBe("False");
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Should.Throw<ArgumentNullException>(() => ((DbContextOptionsBuilder)null!).UseEntityConventions());
        Should.Throw<ArgumentNullException>(() => new ConventionsOptionsBuilder(ConventionsOptions.Default).Clock(null!));
        Should.Throw<ArgumentNullException>(() => ((IQueryable<Post>)null!).IncludeDeleted());
    }

    // ---------------------------------------------------------------------------------------

    private static BlogContext Context(Action<ConventionsOptionsBuilder> configure)
        => new(new DbContextOptionsBuilder<BlogContext>()
            .UseSqlite("Data Source=:memory:")
            .UseEntityConventions(configure)
            .Options);

    private static Microsoft.EntityFrameworkCore.Metadata.IModel Build(Action<ModelBuilder> configure)
    {
        using var context = new AdHocContext(configure);
        return context.Model;
    }

    private sealed class AdHocContext(Action<ModelBuilder> configure) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseSqlite("Data Source=:memory:")
                // Each test builds a different model on the same context type.
                .ReplaceService<IModelCacheKeyFactory, UncachedModelKeys>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => configure(modelBuilder);
    }

    private sealed class UncachedModelKeys : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => new object();
    }
}
