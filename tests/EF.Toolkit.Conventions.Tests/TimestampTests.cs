using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Conventions.Tests;

public sealed class TimestampTests : IAsyncLifetime
{
    private BlogDatabase _db = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _db = await BlogDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task An_insert_stamps_both_timestamps()
    {
        await using var context = _db.Context();
        var post = new Post { Id = 1, Title = "a" };
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);

        post.CreatedAt.ShouldBe(BlogDatabase.Start);
        post.UpdatedAt.ShouldBe(BlogDatabase.Start);
    }

    [Fact]
    public async Task An_insert_keeps_a_time_the_application_set()
    {
        var imported = new DateTimeOffset(2020, 5, 5, 0, 0, 0, TimeSpan.Zero);
        await using var context = _db.Context();
        var post = new Post { Id = 1, Title = "a", CreatedAt = imported };
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);

        post.CreatedAt.ShouldBe(imported);
        post.UpdatedAt.ShouldBe(BlogDatabase.Start);
    }

    [Fact]
    public async Task An_update_moves_UpdatedAt_and_cannot_move_CreatedAt()
    {
        await Insert();
        _db.Clock.Now = BlogDatabase.Start.AddMinutes(30);

        await using (var context = _db.Context())
        {
            var post = await context.Posts.SingleAsync(Ct);
            post.Title = "b";
            post.CreatedAt = DateTimeOffset.MinValue.AddYears(1);
            await context.SaveChangesAsync(Ct);

            post.CreatedAt.ShouldBe(BlogDatabase.Start);
        }

        await using var fresh = _db.Context();
        var row = await fresh.Posts.SingleAsync(Ct);
        row.Title.ShouldBe("b");
        row.CreatedAt.ShouldBe(BlogDatabase.Start);
        row.UpdatedAt.ShouldBe(BlogDatabase.Start.AddMinutes(30));
    }

    [Fact]
    public async Task A_change_made_without_notification_is_still_stamped()
    {
        // The interceptor runs before EF's own DetectChanges, so it has to detect for itself.
        await Insert();
        _db.Clock.Now = BlogDatabase.Start.AddMinutes(1);

        await using (var context = _db.Context())
        {
            (await context.Posts.SingleAsync(Ct)).Title = "changed";
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.SingleAsync(Ct)).UpdatedAt.ShouldBe(BlogDatabase.Start.AddMinutes(1));
    }

    [Fact]
    public async Task An_unchanged_entity_is_left_alone()
    {
        await Insert();
        _db.Clock.Now = BlogDatabase.Start.AddMinutes(1);

        await using var context = _db.Context();
        await context.Posts.SingleAsync(Ct);
        _db.Commands.Clear();
        (await context.SaveChangesAsync(Ct)).ShouldBe(0);
        _db.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shadow_timestamps_need_nothing_on_the_class()
    {
        await using (var context = _db.Context())
        {
            context.Notes.Add(new Note { Id = 1, Body = "x" });
            await context.SaveChangesAsync(Ct);
        }

        _db.Clock.Now = BlogDatabase.Start.AddHours(2);
        await using (var context = _db.Context())
        {
            (await context.Notes.SingleAsync(Ct)).Body = "y";
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        var note = await fresh.Notes.SingleAsync(Ct);
        var entry = fresh.Entry(note);
        entry.Property<DateTime>("CreatedAt").CurrentValue.ShouldBe(BlogDatabase.Start.UtcDateTime);
        entry.Property<DateTime>("UpdatedAt").CurrentValue.ShouldBe(BlogDatabase.Start.AddHours(2).UtcDateTime);
    }

    [Fact]
    public async Task WithoutTimestamps_switches_stamping_off()
    {
        await using var context = _db.Context(configure: c => c.WithoutTimestamps());
        var post = new Post { Id = 1, Title = "a" };
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);

        post.CreatedAt.ShouldBe(default);
        post.UpdatedAt.ShouldBeNull();
    }

    private async Task Insert()
    {
        await using var context = _db.Context();
        context.Posts.Add(new Post { Id = 1, Title = "a" });
        await context.SaveChangesAsync(Ct);
    }
}
