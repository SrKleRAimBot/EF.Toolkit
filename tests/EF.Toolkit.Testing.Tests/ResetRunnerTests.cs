namespace EFToolkit.Testing.Tests;

public sealed class ResetRunnerTests
{
    private static readonly ResetScript Script = new(["off a", "off b"], ["delete"], ["on a", "on b"]);

    private static readonly ResetScript Unit = Script with { Begin = ["begin"], Commit = ["commit"], Rollback = ["rollback"] };

    private static Action<string> Recording(List<string> ran, params string[] failOn)
        => sql =>
        {
            ran.Add(sql);
            if (failOn.Contains(sql))
            {
                throw new InvalidOperationException(sql);
            }
        };

    [Fact]
    public async Task The_commit_is_reported_before_a_post_commit_step_that_fails()
    {
        var after = Unit with { AfterCommit = ["alter"] };
        var ran = new List<string>();
        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(after, Recording(ran, "alter"), _ => 0, () => ran.Add("committed")))
            .Message.ShouldBe(ResetRunner.AfterCommitFailed);
        ran.ShouldBe(["off a", "off b", "begin", "delete", "commit", "committed", "alter", "on b", "on a"]);

        var reported = 0;
        await Should.ThrowAsync<InvalidOperationException>(() => ResetRunner.RunAsync(
            after,
            (sql, _) => sql == "alter" ? throw new InvalidOperationException(sql) : Task.CompletedTask,
            (_, _) => Task.FromResult(0L),
            TestContext.Current.CancellationToken,
            () => reported++));
        reported.ShouldBe(1);
    }

    [Fact]
    public void A_reset_that_rolls_back_never_reports_a_commit()
    {
        var reported = false;
        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(Unit, Recording([], "delete"), _ => 0, () => reported = true));
        reported.ShouldBeFalse();
    }

    [Fact]
    public void A_verify_query_counting_anything_fails_the_reset_and_rolls_it_back()
    {
        var ran = new List<string>();
        var verified = Unit with { Verify = ["check"] };

        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(verified, Recording(ran), sql => sql == "check" ? 1 : 0))
            .Message.ShouldBe(ResetRunner.VerifyFailed);

        ran.ShouldBe(["off a", "off b", "begin", "delete", "rollback", "on b", "on a"]);

        ran.Clear();
        ResetRunner.Run(verified, Recording(ran), _ => 0);
        ran.ShouldBe(["off a", "off b", "begin", "delete", "commit", "on b", "on a"]);
    }

    [Fact]
    public void A_successful_unit_commits_and_never_rolls_back()
    {
        var ran = new List<string>();
        ResetRunner.Run(Unit, Recording(ran), _ => 0);

        ran.ShouldBe(["off a", "off b", "begin", "delete", "commit", "on b", "on a"]);
        Unit.All.ShouldBe(ran);
    }

    [Fact]
    public void A_failing_body_is_rolled_back_before_any_setting_is_restored()
    {
        var ran = new List<string>();
        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(Unit, Recording(ran, "delete"), _ => 0)).Message.ShouldBe("delete");

        ran.ShouldBe(["off a", "off b", "begin", "delete", "rollback", "on b", "on a"]);
    }

    [Fact]
    public void A_failing_commit_is_rolled_back_too()
    {
        var ran = new List<string>();
        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(Unit, Recording(ran, "commit"), _ => 0));

        ran.ShouldBe(["off a", "off b", "begin", "delete", "commit", "rollback", "on b", "on a"]);
    }

    [Fact]
    public void Nothing_is_rolled_back_if_the_unit_never_began()
    {
        var ran = new List<string>();
        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(Unit, Recording(ran, "begin"), _ => 0));

        ran.ShouldBe(["off a", "off b", "begin", "on b", "on a"]);
    }

    [Fact]
    public void One_failed_restore_does_not_stop_the_others_and_both_errors_are_reported()
    {
        var ran = new List<string>();
        var error = Should.Throw<AggregateException>(() => ResetRunner.Run(Unit, Recording(ran, "delete", "on b"), _ => 0));

        ran.ShouldBe(["off a", "off b", "begin", "delete", "rollback", "on b", "on a"]);
        error.InnerExceptions.Select(e => e.Message).ShouldBe(["delete", "on b"]);
    }

    [Fact]
    public void A_restore_failing_after_a_clean_run_is_raised_on_its_own()
    {
        var ran = new List<string>();
        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(Unit, Recording(ran, "on a"), _ => 0)).Message.ShouldBe("on a");

        Should.Throw<AggregateException>(() => ResetRunner.Run(Unit, Recording([], "on a", "on b"), _ => 0))
            .InnerExceptions.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_async_runner_follows_the_same_order()
    {
        var ran = new List<string>();
        var record = Recording(ran, "delete", "on b");
        var error = await Should.ThrowAsync<AggregateException>(() => ResetRunner.RunAsync(
            Unit,
            (sql, _) =>
            {
                record(sql);
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult(0L),
            CancellationToken.None));

        error.InnerExceptions.Select(e => e.Message).ShouldBe(["delete", "on b"]);

        ran.ShouldBe(["off a", "off b", "begin", "delete", "rollback", "on b", "on a"]);
    }

    [Fact]
    public void A_clean_run_restores_everything_in_reverse()
    {
        var ran = new List<string>();
        ResetRunner.Run(Script, ran.Add, _ => 0);

        ran.ShouldBe(["off a", "off b", "delete", "on b", "on a"]);
    }

    [Fact]
    public void A_prologue_failing_half_way_restores_only_what_it_changed()
    {
        var ran = new List<string>();
        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(
            Script,
            sql =>
            {
                if (sql == "off b")
                {
                    throw new InvalidOperationException("boom");
                }

                ran.Add(sql);
            },
            _ => 0));

        ran.ShouldBe(["off a", "on a"]);
    }

    [Fact]
    public void A_failing_body_still_restores_the_whole_prologue()
    {
        var ran = new List<string>();
        Should.Throw<InvalidOperationException>(() => ResetRunner.Run(
            Script,
            sql =>
            {
                ran.Add(sql);
                if (sql == "delete")
                {
                    throw new InvalidOperationException("boom");
                }
            },
            _ => 0));

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
            (_, _) => Task.FromResult(0L),
            cancel.Token));

        restores.ShouldBe([("on a", false)]);
    }
}
