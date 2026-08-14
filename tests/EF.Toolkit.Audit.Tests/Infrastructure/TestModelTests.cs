namespace EFToolkit.Audit.Tests.Infrastructure;

public class TestModelTests
{
    [Fact]
    public void Per_instance_cache_key_is_stable_and_includes_design_time()
    {
        using var context = TestModel.Context();
        var factory = new PerInstanceModelCacheKeyFactory();

        factory.Create(context, false).ShouldBe(factory.Create(context, false));
        factory.Create(context, true).ShouldNotBe(factory.Create(context, false));
    }

    [Fact]
    public void Per_instance_cache_key_differs_between_contexts()
    {
        using var first = TestModel.Context();
        using var second = TestModel.Context();
        var factory = new PerInstanceModelCacheKeyFactory();

        factory.Create(first, false).ShouldNotBe(factory.Create(second, false));
    }
}
