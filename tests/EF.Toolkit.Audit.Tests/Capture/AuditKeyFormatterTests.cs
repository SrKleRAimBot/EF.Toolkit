using EFToolkit.Audit.Capture;

namespace EFToolkit.Audit.Tests.Capture;

public class AuditKeyFormatterTests
{
    [Fact]
    public void Renders_a_single_component_as_itself()
        => AuditKeyFormatter.Format([42]).ShouldBe("42");

    [Fact]
    public void Renders_a_guid_invariantly()
    {
        var id = Guid.Parse("0195c2a5-0000-7000-8000-000000000001");

        AuditKeyFormatter.Format([id]).ShouldBe(id.ToString());
    }

    [Fact]
    public void Renders_a_null_component_as_empty()
        => AuditKeyFormatter.Format([null]).ShouldBe("");

    [Fact]
    public void Renders_bytes_as_base64()
        => AuditKeyFormatter.Format([new byte[] { 1, 2, 3 }]).ShouldBe("AQID");

    [Fact]
    public void Keeps_composite_keys_distinct_when_a_component_contains_the_separator()
    {
        // The failure this prevents is not theoretical: joined naively, both of these render as
        // "a|b|c" and a history query for one row returns the other row's changes.
        var left = AuditKeyFormatter.Format(["a|b", "c"]);
        var right = AuditKeyFormatter.Format(["a", "b|c"]);

        left.ShouldNotBe(right);
    }

    [Fact]
    public void Keeps_composite_keys_distinct_when_a_component_contains_the_escape()
    {
        var left = AuditKeyFormatter.Format([@"a\", "b"]);
        var right = AuditKeyFormatter.Format(["a", @"\b"]);

        left.ShouldNotBe(right);
    }

    [Fact]
    public void Renders_a_composite_key_stably()
    {
        var first = AuditKeyFormatter.Format(["group", "user"]);
        var second = AuditKeyFormatter.Format(["group", "user"]);

        first.ShouldBe(second);
        first.ShouldBe("group|user");
    }

    [Fact]
    public void Uses_invariant_formatting_for_numbers()
    {
        var original = Thread.CurrentThread.CurrentCulture;

        try
        {
            // A culture whose decimal separator is a comma would otherwise render a decimal key
            // differently on a developer's machine than on the server that wrote the row.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            AuditKeyFormatter.Format([12.5m]).ShouldBe("12.5");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
