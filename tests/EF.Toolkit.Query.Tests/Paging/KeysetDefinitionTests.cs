using EFToolkit.Query.Paging;
using EFToolkit.Query.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Tests.Paging;

/// <summary>
///     Covers every ordering a keyset page refuses to walk along. Each refusal stands in for a way a
///     page would otherwise come back wrong without saying so.
/// </summary>
public class KeysetDefinitionTests
{
    [Fact]
    public void A_definition_with_no_components_is_refused()
        => Should.Throw<QueryNotSupportedException>(
                () => KeysetDefinition.For<Order>(static _ => { }))
            .Message.ShouldContain("no components");

    [Fact]
    public void A_nullable_component_is_refused_at_build_time()
    {
        // SQL Server sorts NULLs first ascending and PostgreSQL sorts them last, and a comparison
        // against NULL is neither true nor false — so every row with a NULL there is skipped by every
        // page, on every engine, in a different way.
        var failure = Should.Throw<QueryNotSupportedException>(
            () => KeysetDefinition.For<Shipment>(k => k.Ascending(o => (DateTimeOffset?)o.DispatchedAt)));

        failure.Message.ShouldContain("nullable");
    }

    [Fact]
    public void A_computed_component_is_refused()
        => Should.Throw<QueryNotSupportedException>(
                () => KeysetDefinition.For<Order>(k => k.Ascending(o => o.Total * 2)))
            .Message.ShouldContain("property access");

    [Fact]
    public void The_same_column_twice_is_refused()
        => Should.Throw<QueryNotSupportedException>(
            () => KeysetDefinition.For<Order>(k => k.Ascending(o => o.Id).Descending(o => o.Id)));

    [Fact]
    public void A_column_mapped_as_nullable_is_refused_against_the_model()
    {
        // Note is a nullable string: the CLR type alone cannot say so, which is why this check needs
        // the model rather than only the build-time one.
        using var context = TestModel.Context();
        var keys = KeysetDefinition.For<Order>(k => k.Ascending(o => o.Note!).Ascending(o => o.Id));

        Should.Throw<QueryNotSupportedException>(
                () => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Order))))
            .Message.ShouldContain("nullable");
    }

    [Fact]
    public void A_value_converted_column_is_refused_against_the_model()
    {
        // Status stored as text sorts "Cancelled" before "Placed", which is not the enum's own order —
        // and both the ORDER BY and the page comparison run against the stored value.
        using var context = TestModel.Context(onModelCreating: static b =>
            b.Entity<Order>().Property(x => x.Status).HasConversion<string>().HasMaxLength(16));

        var keys = KeysetDefinition.For<Order>(k => k.Ascending(o => o.Status).Ascending(o => o.Id));

        Should.Throw<QueryNotSupportedException>(
                () => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Order))))
            .Message.ShouldContain("value converter");
    }

    [Fact]
    public void A_value_converted_column_is_accepted_once_the_caller_takes_responsibility()
    {
        using var context = TestModel.Context(onModelCreating: static b =>
            b.Entity<Order>().Property(x => x.Status).HasConversion<string>().HasMaxLength(16));

        var keys = KeysetDefinition.For<Order>(k => k
            .Ascending(o => o.Status)
            .Ascending(o => o.Id)
            .AllowConvertedKey());

        Should.NotThrow(() => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Order))));
    }

    [Fact]
    public void An_ordering_that_cannot_break_every_tie_is_refused()
    {
        // Without a unique final column the boundary falls in an arbitrary place among tied rows, so
        // a row can be returned on two consecutive pages or on neither.
        using var context = TestModel.Context();
        var keys = KeysetDefinition.For<Order>(k => k.Ascending(o => o.PlacedAt));

        Should.Throw<QueryNotSupportedException>(
                () => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Order))))
            .Message.ShouldContain("not total");
    }

    [Fact]
    public void An_ordering_ending_in_the_primary_key_is_accepted()
    {
        using var context = TestModel.Context();
        var keys = KeysetDefinition.For<Order>(k => k.Descending(o => o.PlacedAt).Ascending(o => o.Id));

        Should.NotThrow(() => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Order))));
    }

    [Fact]
    public void An_ordering_covering_a_unique_index_is_accepted()
    {
        using var context = TestModel.Context(onModelCreating: static b =>
            b.Entity<Order>().HasIndex(x => x.Reference).IsUnique());

        var keys = KeysetDefinition.For<Order>(k => k.Ascending(o => o.Reference));

        Should.NotThrow(() => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Order))));
    }

    [Fact]
    public void A_non_unique_index_does_not_make_an_ordering_total()
    {
        using var context = TestModel.Context(onModelCreating: static b =>
            b.Entity<Order>().HasIndex(x => x.Reference));

        var keys = KeysetDefinition.For<Order>(k => k.Ascending(o => o.Reference));

        Should.Throw<QueryNotSupportedException>(
            () => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Order))));
    }

    [Fact]
    public void A_non_total_ordering_is_accepted_once_the_caller_takes_responsibility()
    {
        using var context = TestModel.Context();
        var keys = KeysetDefinition.For<Order>(k => k.Ascending(o => o.PlacedAt).AllowNonUniqueKey());

        Should.NotThrow(() => keys.ValidateAgainst(context.Model.FindEntityType(typeof(Order))));
    }

    [Fact]
    public void A_projection_the_model_knows_nothing_about_is_left_alone()
    {
        // Paging a DTO is legitimate and there is no model to check it against. The checks that need
        // no model have already run at build time.
        var keys = KeysetDefinition.For<OrderSummary>(k => k.Ascending(o => o.Id));

        Should.NotThrow(() => keys.ValidateAgainst(null));
    }

    [Fact]
    public void ColumnPaths_reports_the_ordering_in_priority_order()
        => KeysetDefinition.For<Order>(k => k.Descending(o => o.PlacedAt).Ascending(o => o.Id))
            .ColumnPaths.ShouldBe([nameof(Order.PlacedAt), nameof(Order.Id)]);

    [Fact]
    public void Ordering_backwards_reverses_every_component()
    {
        using var context = TestModel.Context();
        var keys = KeysetDefinition.For<Order>(k => k.Descending(o => o.PlacedAt).Ascending(o => o.Id));

        var forward = keys.Order(context.Orders).Expression.ToString();
        var backward = keys.Order(context.Orders, backward: true).Expression.ToString();

        forward.ShouldContain("OrderByDescending");
        forward.ShouldContain("ThenBy(");
        backward.ShouldContain("OrderBy(");
        backward.ShouldContain("ThenByDescending");
    }

    [Fact]
    public void For_rejects_a_null_configuration()
        => Should.Throw<ArgumentNullException>(() => KeysetDefinition.For<Order>(null!));
}
