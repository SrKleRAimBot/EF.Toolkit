using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Conventions.Tests;

public sealed class SoftDeleteTests : IAsyncLifetime
{
    private BlogDatabase _db = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await BlogDatabase.CreateAsync();
        await using var context = _db.Context();

        var author = new Author
        {
            Id = 1,
            Name = "ann",
            Profile = new AuthorProfile { Bio = "bio", Links = { new ProfileLink { Id = 1, Url = "a" }, new ProfileLink { Id = 2, Url = "b" } } },
        };
        context.Authors.Add(author);
        context.Posts.AddRange(
            new Post { Id = 1, Title = "one", Tenant = 1, Author = author, Seo = { Slug = "one-slug" }, Comments = { new Comment { Id = 1, Text = "c1" }, new Comment { Id = 2, Text = "c2" } } },
            new Post { Id = 2, Title = "two", Tenant = 1, Author = author, Seo = { Slug = "two-slug" } },
            new Post { Id = 3, Title = "three", Tenant = 2, Seo = { Slug = "three-slug" } },
            new FeaturedPost { Id = 4, Title = "four", Tenant = 1, Rank = 1, Seo = { Slug = "four-slug" } });
        context.Notes.Add(new Note { Id = 1, Body = "n" });
        await context.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    // ---------------------------------------------------------------------------------------
    // Remove() becomes an update
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_marks_the_row_instead_of_deleting_it()
    {
        await using (var context = _db.Context())
        {
            var post = await context.Posts.SingleAsync(p => p.Id == 2, Ct);
            context.Posts.Remove(post);
            await context.SaveChangesAsync(Ct);

            // As after a real delete, the instance is no longer tracked.
            context.Entry(post).State.ShouldBe(EntityState.Detached);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.AnyAsync(p => p.Id == 2, Ct)).ShouldBeFalse();

        var row = await fresh.Posts.IncludeDeleted().SingleAsync(p => p.Id == 2, Ct);
        row.IsDeleted.ShouldBeTrue();
        row.Title.ShouldBe("two");
    }

    [Fact]
    public async Task Only_the_flag_and_the_updated_timestamp_are_written()
    {
        await using var context = _db.Context();
        var post = await context.Posts.SingleAsync(p => p.Id == 2, Ct);
        post.Title = "edited but then removed";
        context.Posts.Remove(post);

        _db.Commands.Clear();
        await context.SaveChangesAsync(Ct);

        var update = _db.Commands.ShouldHaveSingleItem(string.Join("\n---\n", _db.Commands));
        update.ShouldContain("\"IsDeleted\"");
        update.ShouldContain("\"UpdatedAt\"");
        update.ShouldNotContain("\"Title\"");
        _db.Commands.ShouldNotContain(c => c.Contains("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_timestamp_property_records_when_the_row_was_deleted_in_utc()
    {
        await using (var context = _db.Context())
        {
            _db.Clock.Now = BlogDatabase.Start.AddHours(5);
            context.Comments.Remove(await context.Comments.SingleAsync(c => c.Id == 2, Ct));
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        var comment = await fresh.Comments.IncludeDeleted().SingleAsync(c => c.Id == 2, Ct);
        comment.DeletedAt.ShouldBe(BlogDatabase.Start.AddHours(5).UtcDateTime);
        (await fresh.Comments.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task The_synchronous_save_converts_too()
    {
        await using (var context = _db.Context())
        {
            context.Posts.Remove(context.Posts.Single(p => p.Id == 1));
            context.SaveChanges();
        }

        await using var fresh = _db.Context();
        fresh.Posts.IncludeDeleted().Single(p => p.Id == 1).IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task A_derived_type_is_soft_deleted_through_its_root()
    {
        await using (var context = _db.Context())
        {
            context.Remove(await context.Posts.OfType<FeaturedPost>().SingleAsync(Ct));
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.OfType<FeaturedPost>().AnyAsync(Ct)).ShouldBeFalse();
        (await fresh.Posts.IncludeDeleted().OfType<FeaturedPost>().SingleAsync(Ct)).Rank.ShouldBe(1);
    }

    [Fact]
    public async Task Removing_an_untracked_entity_marks_it_without_loading()
    {
        await using (var context = _db.Context())
        {
            context.Posts.Remove(new Post { Id = 3, Title = "three", Tenant = 2, Seo = { Slug = "three-slug" } });
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.IncludeDeleted().SingleAsync(p => p.Id == 3, Ct)).IsDeleted.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Cascades Remove() already applied
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Loaded_soft_deletable_dependents_are_soft_deleted_with_their_principal()
    {
        await using (var context = _db.Context())
        {
            var post = await context.Posts.Include(p => p.Comments).SingleAsync(p => p.Id == 1, Ct);
            context.Posts.Remove(post);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        var comments = await fresh.Comments.IncludeDeleted().OrderBy(c => c.Id).ToListAsync(Ct);
        comments.Count.ShouldBe(2);
        comments.ShouldAllBe(c => c.DeletedAt != null);
    }

    [Fact]
    public async Task An_owned_type_in_the_same_row_keeps_its_values()
    {
        await using (var context = _db.Context())
        {
            context.Posts.Remove(await context.Posts.SingleAsync(p => p.Id == 2, Ct));
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.IncludeDeleted().SingleAsync(p => p.Id == 2, Ct)).Seo.Slug.ShouldBe("two-slug");
    }

    [Fact]
    public async Task A_nested_owned_collection_survives_the_soft_delete_and_the_restore()
    {
        // Remove() marks the whole ownership subtree deleted; the links are owned by the profile, not
        // by the author directly, and live in a table of their own.
        await using (var context = _db.Context())
        {
            context.Authors.Remove(await context.Authors.SingleAsync(Ct));
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = _db.Context())
        {
            var deleted = await context.Authors.IncludeDeleted().AsNoTracking().SingleAsync(Ct);
            deleted.Profile!.Bio.ShouldBe("bio");
            deleted.Profile.Links.Select(l => l.Url).Order().ShouldBe(["a", "b"]);

            context.Restore(await context.Authors.OnlyDeleted().SingleAsync(Ct));
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        var restored = await fresh.Authors.SingleAsync(Ct);
        restored.Profile!.Links.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Restore_after_Remove_keeps_a_nested_owned_collection()
    {
        await using (var context = _db.Context())
        {
            var author = await context.Authors.SingleAsync(Ct);
            context.Authors.Remove(author);
            context.Restore(author);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Authors.SingleAsync(Ct)).Profile!.Links.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_optional_foreign_key_nulled_by_the_remove_is_put_back()
    {
        await using (var context = _db.Context())
        {
            var author = await context.Authors.Include(a => a.Posts).SingleAsync(Ct);
            var post = author.Posts.Single(p => p.Id == 1);

            context.Authors.Remove(author);
            post.AuthorId.ShouldBeNull();   // EF's fixup, before any save

            await context.SaveChangesAsync(Ct);

            post.AuthorId.ShouldBe(1);
            context.Entry(post).State.ShouldBe(EntityState.Unchanged);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.Where(p => p.AuthorId == 1).CountAsync(Ct)).ShouldBe(2);
        (await fresh.Authors.AnyAsync(Ct)).ShouldBeFalse();
        (await fresh.Authors.IncludeDeleted().AnyAsync(Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_foreign_key_the_application_nulled_itself_stays_null()
    {
        await using (var context = _db.Context())
        {
            var post = await context.Posts.SingleAsync(p => p.Id == 2, Ct);
            post.AuthorId = null;
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.SingleAsync(p => p.Id == 2, Ct)).AuthorId.ShouldBeNull();
    }

    // ---------------------------------------------------------------------------------------
    // Reading past the filter
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task IncludeDeleted_keeps_every_other_named_filter()
    {
        await SoftDelete(3);   // tenant 2's only post

        await using var tenant2 = _db.Context(tenant: 2);
        (await tenant2.Posts.CountAsync(Ct)).ShouldBe(0);
        (await tenant2.Posts.IncludeDeleted().Select(p => p.Id).ToListAsync(Ct)).ShouldBe([3]);
    }

    [Fact]
    public async Task OnlyDeleted_is_a_recycle_bin()
    {
        await SoftDelete(1, 2);

        await using var context = _db.Context();
        (await context.Posts.OnlyDeleted().OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(Ct)).ShouldBe([1, 2]);
        (await context.Notes.CountAsync(Ct)).ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => context.Notes.OnlyDeleted())
            .Message.ShouldContain("'Note' is not soft-deletable");
    }

    [Fact]
    public async Task IsSoftDeleted_reads_the_values_an_entity_carries()
    {
        await using var context = _db.Context();

        context.IsSoftDeleted(new Post { IsDeleted = true }).ShouldBeTrue();
        context.IsSoftDeleted(new Post()).ShouldBeFalse();
        context.IsSoftDeleted(new Comment { DeletedAt = DateTime.UtcNow }).ShouldBeTrue();
        context.IsSoftDeleted(new Comment()).ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------------------
    // Restore and hard delete
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Restore_brings_a_deleted_row_back()
    {
        await SoftDelete(2);

        await using (var context = _db.Context())
        {
            var post = await context.Posts.OnlyDeleted().SingleAsync(Ct);
            context.Restore(post);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.AnyAsync(p => p.Id == 2, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Restore_attaches_an_untracked_instance()
    {
        await SoftDelete(2);

        await using (var context = _db.Context())
        {
            var post = await context.Posts.OnlyDeleted().AsNoTracking().SingleAsync(Ct);
            context.Restore(post).State.ShouldBe(EntityState.Modified);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.AnyAsync(p => p.Id == 2, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Restore_after_Remove_in_the_same_unit_of_work_cancels_it()
    {
        await using (var context = _db.Context())
        {
            var post = await context.Posts.SingleAsync(p => p.Id == 2, Ct);
            context.Posts.Remove(post);
            context.Restore(post);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.AnyAsync(p => p.Id == 2, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task HardDelete_removes_the_row_for_real()
    {
        await using (var context = _db.Context())
        {
            var post = await context.Posts.SingleAsync(p => p.Id == 2, Ct);
            context.HardDelete(post).State.ShouldBe(EntityState.Deleted);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.IncludeDeleted().AnyAsync(p => p.Id == 2, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_hard_delete_mark_is_spent_by_the_save_that_used_it()
    {
        await using var context = _db.Context();
        var post = await context.Posts.SingleAsync(p => p.Id == 2, Ct);
        context.HardDelete(post);
        await context.SaveChangesAsync(Ct);

        // The same instance, inserted again and then removed, gets the ordinary treatment.
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);
        context.Posts.Remove(post);
        await context.SaveChangesAsync(Ct);

        await using var fresh = _db.Context();
        (await fresh.Posts.IncludeDeleted().SingleAsync(p => p.Id == 2, Ct)).IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task HardDelete_works_on_a_context_without_the_conventions()
    {
        await using (var context = _db.Context(conventions: false))
        {
            context.HardDelete(await context.Posts.SingleAsync(p => p.Id == 2, Ct));
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.IncludeDeleted().AnyAsync(p => p.Id == 2, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Without_soft_delete_Remove_deletes_but_the_filter_still_hides()
    {
        await SoftDelete(1);

        await using (var context = _db.Context(configure: c => c.WithoutSoftDelete()))
        {
            (await context.Posts.AnyAsync(p => p.Id == 1, Ct)).ShouldBeFalse();
            context.Posts.Remove(await context.Posts.SingleAsync(p => p.Id == 2, Ct));
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.IncludeDeleted().AnyAsync(p => p.Id == 2, Ct)).ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------------------
    // A failed save
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_save_leaves_the_delete_as_the_application_made_it()
    {
        await using var context = _db.Context();
        var post = await context.Posts.SingleAsync(p => p.Id == 2, Ct);
        context.Posts.Remove(post);
        context.Posts.Add(new Post { Id = 10, Title = "one", Tenant = 1 });   // unique index violation

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));

        context.Entry(post).State.ShouldBe(EntityState.Deleted);
        post.IsDeleted.ShouldBeFalse();

        // ...so a corrected retry converts it again.
        context.ChangeTracker.Entries<Post>().Single(e => e.Entity.Id == 10).State = EntityState.Detached;
        await context.SaveChangesAsync(Ct);

        await using var fresh = _db.Context();
        (await fresh.Posts.IncludeDeleted().SingleAsync(p => p.Id == 2, Ct)).IsDeleted.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Set-based
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SoftDeleteWhere_marks_matching_rows_in_one_statement()
    {
        _db.Clock.Now = BlogDatabase.Start.AddDays(1);
        await using var context = _db.Context();

        _db.Commands.Clear();
        (await context.Posts.SoftDeleteWhereAsync(p => p.Tenant == 1, Ct)).ShouldBe(3);
        _db.Commands.Count.ShouldBe(1);

        var rows = await context.Posts.IncludeDeleted().AsNoTracking().OrderBy(p => p.Id).ToListAsync(Ct);
        rows.Select(p => p.IsDeleted).ShouldBe([true, true, false, true]);
        rows[0].UpdatedAt.ShouldBe(BlogDatabase.Start.AddDays(1));
        rows[2].UpdatedAt.ShouldBe(BlogDatabase.Start);
    }

    [Fact]
    public async Task SoftDeleteWhere_does_not_restamp_rows_already_deleted()
    {
        await using var context = _db.Context();
        (await context.Comments.SoftDeleteWhereAsync(c => c.Id == 1, Ct)).ShouldBe(1);

        _db.Clock.Now = BlogDatabase.Start.AddDays(3);
        (await context.Comments.SoftDeleteWhereAsync(c => true, Ct)).ShouldBe(1);

        var first = await context.Comments.IncludeDeleted().AsNoTracking().SingleAsync(c => c.Id == 1, Ct);
        first.DeletedAt.ShouldBe(BlogDatabase.Start.UtcDateTime);
    }

    [Fact]
    public async Task RestoreWhere_restores_only_deleted_matching_rows()
    {
        await SoftDelete(1, 2, 3);
        await using var context = _db.Context();

        (await context.Posts.RestoreWhereAsync(p => p.Tenant == 1, Ct)).ShouldBe(2);
        context.Posts.RestoreWhere(p => p.Id == 3).ShouldBe(1);
        context.Posts.SoftDeleteWhere(p => p.Id == 4).ShouldBe(1);

        (await context.Posts.OnlyDeleted().Select(p => p.Id).ToListAsync(Ct)).ShouldBe([4]);
    }

    [Fact]
    public async Task Set_based_writes_refuse_a_type_without_soft_delete()
    {
        await using var context = _db.Context();
        await Should.ThrowAsync<InvalidOperationException>(() => context.Notes.SoftDeleteWhereAsync(n => true, Ct));
    }

    // ---------------------------------------------------------------------------------------

    private async Task SoftDelete(params int[] ids)
    {
        await using var context = _db.Context();
        context.Posts.RemoveRange(await context.Posts.Where(p => ids.Contains(p.Id)).ToListAsync(Ct));
        await context.SaveChangesAsync(Ct);
    }
}
