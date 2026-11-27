using System.Linq.Expressions;
using EFToolkit.Bulk.Api;

namespace EFToolkit.Bulk.Tests.Api;

/// <summary>
///     The translation from an object initializer to setters, in isolation. Every refusal here is a
///     shape that would otherwise lose an assignment — or invent one — on its way into the SET clause.
/// </summary>
public class ValueUpdateTranslatorTests
{
    [Fact]
    public void Each_assignment_becomes_one_setter_over_the_same_row_parameter()
    {
        var setters = ValueUpdateTranslator.Translate<Ticket>(t => new Ticket { Title = "x", Points = t.Points + 1 });

        setters.Count.ShouldBe(2);
        setters[0].Property.ToString().ShouldBe("t => t.Title");
        setters[0].Value.ToString().ShouldBe("t => \"x\"");
        setters[1].Property.ToString().ShouldBe("t => t.Points");
        setters[1].Value.ToString().ShouldBe("t => (t.Points + 1)");

        foreach (var setter in setters)
        {
            // EF requires the selector and the value to share their parameter.
            setter.Value.Parameters.Single().ShouldBeSameAs(setter.Property.Parameters.Single());
            setter.Property.ReturnType.ShouldBe(setter.Value.ReturnType);
        }
    }

    [Fact]
    public void A_nested_initializer_becomes_a_member_chain()
    {
        var setters = ValueUpdateTranslator.Translate<Ticket>(
            t => new Ticket { Location = { City = "a", Street = t.Title }, Points = 1 });

        setters.Select(s => s.Property.ToString())
            .ShouldBe(["t => t.Location.City", "t => t.Location.Street", "t => t.Points"]);
    }

    [Fact]
    public void An_assignment_that_needs_a_conversion_keeps_the_member_type()
    {
        // 'Score = 1' on a long? member: the compiler converts the int, and the setter must be typed
        // as the member, or SetProperty<TProperty> is instantiated with the wrong TProperty.
        var setters = ValueUpdateTranslator.Translate<Scored>(s => new Scored { Score = 1 });

        setters.Single().Property.ReturnType.ShouldBe(typeof(long?));
        setters.Single().Value.ReturnType.ShouldBe(typeof(long?));
    }

    [Fact]
    public void A_hand_built_tree_with_a_mismatched_value_type_is_converted()
    {
        var row = Expression.Parameter(typeof(Scored), "s");
        var body = Expression.MemberInit(
            Expression.New(typeof(Scored)),
            Expression.Bind(typeof(Scored).GetProperty(nameof(Scored.Score))!, Expression.Convert(Expression.Constant(3), typeof(long?))));

        var setters = ValueUpdateTranslator.Translate(Expression.Lambda<Func<Scored, Scored>>(body, row));

        setters.Single().Value.ReturnType.ShouldBe(typeof(long?));
    }

    [Fact]
    public void Anything_but_an_initializer_is_refused()
    {
        var error = Should.Throw<ArgumentException>(() => ValueUpdateTranslator.Translate<Ticket>(t => t));

        error.ParamName.ShouldBe("valueUpdates");
        error.Message.ShouldContain("must be an object initializer");
        error.Message.ShouldContain("new Ticket { Column = value }");
    }

    [Fact]
    public void An_initializer_without_assignments_is_refused()
    {
        Should.Throw<ArgumentException>(() => ValueUpdateTranslator.Translate<Ticket>(t => new Ticket()))
            .Message.ShouldContain("assigns no members");
    }

    [Fact]
    public void Constructor_arguments_are_refused()
    {
        Should.Throw<ArgumentException>(
                () => ValueUpdateTranslator.Translate<Positional>(p => new Positional(p.Name) { Age = 3 }))
            .Message.ShouldContain("constructor arguments");
    }

    [Fact]
    public void A_derived_type_is_refused_with_the_OfType_hint()
    {
        var error = Should.Throw<ArgumentException>(
            () => ValueUpdateTranslator.Translate<Animal>(a => new Dog { Name = "rex" }));

        error.Message.ShouldContain("constructs 'Dog', not 'Animal'");
        error.Message.ShouldContain("OfType<Dog>()");
    }

    [Fact]
    public void A_collection_initializer_is_refused()
    {
        Should.Throw<ArgumentException>(
                () => ValueUpdateTranslator.Translate<Tagged>(t => new Tagged { Tags = { "a" } }))
            .Message.ShouldContain("collection initializer on 'Tags'");
    }

    [Fact]
    public void A_member_assigned_twice_in_a_hand_built_tree_is_refused()
    {
        var row = Expression.Parameter(typeof(Ticket), "t");
        var points = typeof(Ticket).GetProperty(nameof(Ticket.Points))!;
        var body = Expression.MemberInit(
            Expression.New(typeof(Ticket)),
            Expression.Bind(points, Expression.Constant(1)),
            Expression.Bind(points, Expression.Constant(2)));

        Should.Throw<ArgumentException>(
                () => ValueUpdateTranslator.Translate(Expression.Lambda<Func<Ticket, Ticket>>(body, row)))
            .Message.ShouldContain("more than once");
    }

    [Fact]
    public void Null_is_refused()
        => Should.Throw<ArgumentNullException>(() => ValueUpdateTranslator.Translate<Ticket>(null!));

    public sealed class Scored
    {
        public long? Score { get; set; }
    }

    public sealed class Positional(string name)
    {
        public string Name { get; } = name;
        public int Age { get; set; }
    }

    public class Animal
    {
        public string Name { get; set; } = "";
    }

    public sealed class Dog : Animal;

    public sealed class Tagged
    {
        public List<string> Tags { get; } = [];
    }
}
