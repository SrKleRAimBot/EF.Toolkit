using EFToolkit.Audit.Api;

namespace EFToolkit.Audit.Tests.Api;

public class AuditScopeTests
{
    [Fact]
    public void Is_absent_until_one_is_begun()
        => AuditScope.Current.ShouldBeNull();

    [Fact]
    public void Carries_the_actor_and_reason_it_was_given()
    {
        using var scope = AuditScope.Begin(new AuditActor("u1", "Ada"), reason: "cleanup");

        AuditScope.Current.ShouldBe(scope);
        scope.Actor!.Value.Id.ShouldBe("u1");
        scope.Reason.ShouldBe("cleanup");
    }

    [Fact]
    public void Correlates_entries_without_being_asked_to()
    {
        using var scope = AuditScope.Begin("importer");

        // Every entry written inside a scope should be tied to the rest of its unit of work, and
        // requiring the caller to think of that is how it ends up null in production.
        scope.CorrelationId.ShouldNotBeNull();
    }

    [Fact]
    public void Inherits_what_a_nested_scope_does_not_state()
    {
        using var outer = AuditScope.Begin("importer", reason: "nightly").With("run", 3);
        using var inner = AuditScope.Begin(new AuditActor("u2"));

        inner.Reason.ShouldBe("nightly");
        inner.CorrelationId.ShouldBe(outer.CorrelationId);
        inner.Actor!.Value.Id.ShouldBe("u2");
        inner.Metadata["run"].ShouldBe(3);
    }

    [Fact]
    public void Restores_the_outer_scope_on_dispose()
    {
        using var outer = AuditScope.Begin("outer");

        using (AuditScope.Begin("inner"))
        {
            AuditScope.Current!.Actor!.Value.Id.ShouldBe("inner");
        }

        AuditScope.Current!.Actor!.Value.Id.ShouldBe("outer");
    }

    [Fact]
    public void Leaves_no_scope_behind()
    {
        using (AuditScope.Begin("only"))
        {
            AuditScope.Current.ShouldNotBeNull();
        }

        AuditScope.Current.ShouldBeNull();
    }

    [Fact]
    public void Ignores_a_second_dispose()
    {
        var outer = AuditScope.Begin("outer");
        var inner = AuditScope.Begin("inner");

        inner.Dispose();
        inner.Dispose();

        // Without the guard, the second dispose would restore "outer"'s parent — null — and end a
        // scope that is still in use.
        AuditScope.Current.ShouldBe(outer);

        outer.Dispose();
    }

    [Fact]
    public async Task Does_not_leak_across_asynchronous_flows()
    {
        var seen = await Task.Run(() => AuditScope.Current);

        seen.ShouldBeNull();
    }

    [Fact]
    public void Merges_metadata_over_the_scope_it_nests_in()
    {
        using var outer = AuditScope.Begin("outer").With("a", 1).With("b", 2);
        using var inner = AuditScope.Begin("inner").With("b", 3).With("c", 4);

        inner.Metadata["a"].ShouldBe(1);
        inner.Metadata["b"].ShouldBe(3);
        inner.Metadata["c"].ShouldBe(4);

        // The outer scope is not retroactively changed by what the inner one added.
        outer.Metadata.ContainsKey("c").ShouldBeFalse();
    }

    [Fact]
    public void Refuses_a_blank_metadata_key()
    {
        using var scope = AuditScope.Begin("actor");

        Should.Throw<ArgumentException>(() => scope.With("  ", 1));
    }

    [Fact]
    public void Empty_metadata_cannot_be_mutated_through_its_public_interface()
    {
        using var scope = AuditScope.Begin("actor");
        var metadata = (IDictionary<string, object?>)scope.Metadata;

        Should.Throw<NotSupportedException>(() => metadata.Add("leaked", true));
        scope.Metadata.ShouldBeEmpty();
    }
}
