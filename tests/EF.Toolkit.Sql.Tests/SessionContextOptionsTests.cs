using EFToolkit.Sql.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Sql.Tests;

public sealed class SessionContextOptionsTests
{
    [Fact]
    public void UseSessionContext_registers_the_extension_and_one_interceptor()
    {
        var builder = new DbContextOptionsBuilder()
            .UseNpgsql("Host=unused")
            .UseSessionContext(s => s.Set("app.tenant_id", () => 1))
            .UseSessionContext(s => s.Set("app.user_id", _ => "u"));

        var extension = builder.Options.FindExtension<SessionContextOptionsExtension>()!;
        extension.Keys.ShouldBe(["app.tenant_id", "app.user_id"]);

        builder.Options.FindExtension<CoreOptionsExtension>()!.Interceptors!
            .Count(i => i is SessionContextInterceptor).ShouldBe(1);
    }

    [Fact]
    public void A_key_set_again_in_another_case_is_the_same_key()
    {
        // PostgreSQL and MySQL ignore case in these names: kept apart, both were sent and the one sent
        // last won, whichever had been set last.
        var builder = new DbContextOptionsBuilder()
            .UseNpgsql("Host=unused")
            .UseSessionContext(s => s.Set("app.tenant_id", () => 1).Set("APP.TENANT_ID", () => 2).Set("app.tenant_id", () => 3));

        var values = builder.Options.FindExtension<SessionContextOptionsExtension>()!.Values;

        values.ShouldHaveSingleItem().Resolve(null).ShouldBe(3);
    }

    [Fact]
    public void A_key_set_twice_keeps_the_later_value_in_its_original_place()
    {
        var builder = new DbContextOptionsBuilder()
            .UseNpgsql("Host=unused")
            .UseSessionContext(s => s.Set("app.a", () => 1).Set("app.b", () => 2))
            .UseSessionContext(s => s.Set("app.a", () => 3));

        var values = builder.Options.FindExtension<SessionContextOptionsExtension>()!.Values;

        values.Select(v => v.Key).ShouldBe(["app.a", "app.b"]);
        values[0].Resolve(null).ShouldBe(3);
    }

    [Fact]
    public void The_generic_overload_returns_the_typed_builder()
    {
        var builder = new DbContextOptionsBuilder<DbContext>().UseNpgsql("Host=unused");

        builder.UseSessionContext(s => s.Set("app.a", () => 1)).ShouldBeSameAs(builder);
    }

    [Fact]
    public void Arguments_are_required()
    {
        Should.Throw<ArgumentNullException>(() => ((DbContextOptionsBuilder)null!).UseSessionContext(_ => { }));
        Should.Throw<ArgumentNullException>(() => new DbContextOptionsBuilder().UseSessionContext(null!));
        Should.Throw<ArgumentException>(() => new DbContextOptionsBuilder().UseSessionContext(s => s.Set(" ", () => 1)));
        Should.Throw<ArgumentNullException>(() => new DbContextOptionsBuilder().UseSessionContext(s => s.Set("a.b", (Func<object?>)null!)));
        Should.Throw<ArgumentNullException>(() => new DbContextOptionsBuilder().UseSessionContext(s => s.Set("a.b", (Func<IServiceProvider, object?>)null!)));
    }

    [Fact]
    public void Values_from_services_need_a_service_provider()
    {
        var value = new SessionContextValue("app.a", sp => sp.GetService(typeof(string)));

        value.RequiresServices.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => value.Resolve(null)).Message.ShouldContain("AddDbContext");
        new SessionContextValue("app.a", () => 1).RequiresServices.ShouldBeFalse();
    }

    [Fact]
    public void The_extension_describes_itself_without_splitting_the_service_provider()
    {
        var a = Extension(new DbContextOptionsBuilder().UseNpgsql("Host=x").UseSessionContext(s => s.Set("app.a", () => 1)));
        var b = Extension(new DbContextOptionsBuilder().UseNpgsql("Host=x").UseSessionContext(s => s.Set("app.b", () => 2)));

        a.Info.IsDatabaseProvider.ShouldBeFalse();
        a.Info.LogFragment.ShouldContain("keys=app.a");
        a.Info.GetServiceProviderHashCode().ShouldBe(b.Info.GetServiceProviderHashCode());
        a.Info.ShouldUseSameServiceProvider(b.Info).ShouldBeTrue();

        var debug = new Dictionary<string, string>();
        a.Info.PopulateDebugInfo(debug);
        debug["EF.Toolkit.Sql:SessionContextKeys"].ShouldBe("app.a");

        a.ApplyServices(new Microsoft.Extensions.DependencyInjection.ServiceCollection());
    }

    [Fact]
    public void Validation_refuses_a_bad_key_for_the_configured_provider()
    {
        var options = new DbContextOptionsBuilder().UseNpgsql("Host=x").UseSessionContext(s => s.Set("tenant", () => 1)).Options;

        Should.Throw<ArgumentException>(() => options.FindExtension<SessionContextOptionsExtension>()!.Validate(options));
    }

    [Fact]
    public void Validation_refuses_sqlite()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder().UseSqlite(connection).UseSessionContext(s => s.Set("k", () => 1)).Options;

        Should.Throw<SqlFeatureNotSupportedException>(() => options.FindExtension<SessionContextOptionsExtension>()!.Validate(options))
            .Dialect.ShouldBe(SqlDialect.Sqlite);
    }

    [Fact]
    public void Validation_without_a_provider_is_left_to_EF()
    {
        var options = new DbContextOptionsBuilder().UseSessionContext(s => s.Set("k", () => 1)).Options;

        options.FindExtension<SessionContextOptionsExtension>()!.Validate(options);
    }

    [Fact]
    public void The_interceptor_is_a_connection_interceptor()
        => SessionContextInterceptor.Instance.ShouldBeAssignableTo<IDbConnectionInterceptor>();

    private static SessionContextOptionsExtension Extension(DbContextOptionsBuilder builder)
        => builder.Options.FindExtension<SessionContextOptionsExtension>()!;
}
