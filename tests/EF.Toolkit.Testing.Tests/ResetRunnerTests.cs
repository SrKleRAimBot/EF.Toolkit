namespace EFToolkit.Testing.Tests;

public sealed class ResetRunnerTests
{
    private static readonly ResetScript Script = new(["off a", "off b"], ["delete"], ["on a", "on b"]);

    [Fact]
    public void A_clean_run_restores_everything_in_reverse()
    {
        var ran = new List<string>();
        ResetRunner.Run(Script, ran.Add);

        ran.ShouldBe(["off a", "off b", "delete", "on b", "on a"]);
    }

    [Fact]
    public void A_prologue_failing_half_way_restores_only_what_it_changed()
    {
        var ran = new List<string>();
        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(Script, sql =>
        {
            if (sql == "off b")
            {
                throw new InvalidOperationException("boom");
            }

            ran.Add(sql);
        }));

        ran.ShouldBe(["off a", "on a"]);
    }

    [Fact]
    public void A_failing_body_still_restores_the_whole_prologue()
    {
        var ran = new List<string>();
        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(Script, sql =>
        {
            ran.Add(sql);
            if (sql == "delete")
            {
                throw new InvalidOperationException("boom");
            }
        }));

        ran.ShouldBe(["off a", "off b", "delete", "on b", "on a"]);
    }

    [Fact]
    public async Task Cancelling_during_the_prologue_restores_with_an_uncancellable_token()
    {
        using var cancel = new CancellationTokenSource();
        var restores = new List<(string Sql, bool Cancellable)>();

        await Should.ThrowAsync<OperationCanceledException>(() => ResetRunner.RunAsync(
            Script,
            (sql, token) =>
            {
                if (sql.StartsWith("on", StringComparison.Ordinal))
                {
                    restores.Add((sql, token.CanBeCanceled));
                    return Task.CompletedTask;
                }

                if (sql == "off b")
                {
                    cancel.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return Task.CompletedTask;
            },
            cancel.Token));

        restores.ShouldBe([("on a", false)]);
    }
}
