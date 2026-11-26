using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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
        context.Notes.Add(new Note { Id = 1, Body = "n", Author = author });
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
    public async Task Restore_after_Remove_puts_back_the_keys_Remove_nulled_on_loaded_dependents()
    {
        // Under Immediate timing Remove() nulled each loaded post's AuthorId at once; cancelling the
        // removal must cancel that too, or the save unlinks posts from an author who stays.
        await using (var context = _db.Context())
        {
            var author = await context.Authors.Include(a => a.Posts).SingleAsync(Ct);
            context.Authors.Remove(author);
            author.Posts.ShouldAllBe(p => p.AuthorId == null);   // EF's fixup, before any save

            context.Restore(author);
            author.Posts.ShouldAllBe(p => p.AuthorId == 1);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.CountAsync(p => p.AuthorId == 1, Ct)).ShouldBe(2);
        (await fresh.Authors.AnyAsync(Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Under_OnSaveChanges_timing_a_reparented_dependent_survives_Remove_then_Restore_of_its_new_principal()
    {
        // Under Immediate timing this is the one sequence the repair cannot follow: the key's value
        // before Remove()'s fixup — the new principal — is kept nowhere EF exposes. Under OnSaveChanges
        // there is no fixup to undo.
        await using (var context = _db.Context())
        {
            context.ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
            context.ChangeTracker.DeleteOrphansTiming = CascadeTiming.OnSaveChanges;
            var newAuthor = new Author { Id = 2, Name = "bo" };
            context.Authors.Add(newAuthor);
            await context.SaveChangesAsync(Ct);

            var post = await context.Posts.SingleAsync(p => p.Id == 1, Ct);
            post.AuthorId = 2;
            context.ChangeTracker.DetectChanges();

            context.Authors.Remove(newAuthor);
            context.Restore(newAuthor);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.SingleAsync(p => p.Id == 1, Ct)).AuthorId.ShouldBe(2);
        (await fresh.Authors.CountAsync(Ct)).ShouldBe(2);
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
    public async Task Under_OnSaveChanges_timing_a_key_the_application_nulled_stays_null_when_its_principal_is_removed()
    {
        // The case Immediate timing cannot tell apart from Remove()'s own fixup.
        await using (var context = _db.Context())
        {
            context.ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
            var author = await context.Authors.Include(a => a.Posts).SingleAsync(Ct);
            author.Posts.Single(p => p.Id == 1).AuthorId = null;
            context.ChangeTracker.DetectChanges();

            context.Authors.Remove(author);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.SingleAsync(p => p.Id == 1, Ct)).AuthorId.ShouldBeNull();
        (await fresh.Posts.SingleAsync(p => p.Id == 2, Ct)).AuthorId.ShouldBe(1);
        (await fresh.Authors.AnyAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Under_OnSaveChanges_timing_nothing_cascades_and_owned_data_is_kept()
    {
        await using (var context = _db.Context())
        {
            context.ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
            context.ChangeTracker.DeleteOrphansTiming = CascadeTiming.OnSaveChanges;
            var author = await context.Authors.Include(a => a.Posts).SingleAsync(Ct);
            context.Authors.Remove(author);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.CountAsync(p => p.AuthorId == 1, Ct)).ShouldBe(2);
        (await fresh.Authors.IncludeDeleted().SingleAsync(Ct)).Profile!.Links.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Under_Immediate_timing_a_ClientNoAction_key_the_application_nulled_stays_null()
    {
        // EF's fixup never nulls a ClientNoAction key, so this null can only be the application's.
        await using (var context = _db.Context())
        {
            var note = await context.Notes.Include(n => n.Author).SingleAsync(Ct);
            note.AuthorId = null;
            context.ChangeTracker.DetectChanges();

            context.Authors.Remove(await context.Authors.SingleAsync(Ct));
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Notes.SingleAsync(Ct)).AuthorId.ShouldBeNull();
        (await fresh.Posts.CountAsync(p => p.AuthorId == 1, Ct)).ShouldBe(2);
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
    public async Task A_hard_delete_saved_without_accepting_survives_a_rolled_back_retry()
    {
        // The EF retry pattern: SaveChanges(false) inside a transaction, roll back, save again. The
        // first save cleared the mark, so the retry soft-deleted the row it was asked to purge.
        await using var context = _db.Context();
        var post = await context.Posts.SingleAsync(p => p.Id == 2, Ct);
        context.HardDelete(post);

        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);
            await transaction.RollbackAsync(Ct);
        }

        context.Entry(post).State.ShouldBe(EntityState.Deleted);
        await context.SaveChangesAsync(Ct);

        await using var fresh = _db.Context();
        (await fresh.Posts.IncludeDeleted().AnyAsync(p => p.Id == 2, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Restore_cancels_a_HardDelete_so_a_later_Remove_soft_deletes()
    {
        await using (var context = _db.Context())
        {
            var post = await context.Posts.SingleAsync(p => p.Id == 2, Ct);
            context.HardDelete(post);
            context.Restore(post);
            context.Posts.Remove(post);
            await context.SaveChangesAsync(Ct);
        }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_suppressed_concurrency_conflict_leaves_the_rest_of_the_save_as_soft_deletes(bool async)
    {
        // Another interceptor may suppress a conflict — the row is already gone, say — and EF then goes
        // on to complete the save. Restoring the tracker when the conflict was first reported undid the
        // soft delete of comment 1, which EF then accepted as a real one.
        await using var context = _db.Context(interceptors: new SuppressConcurrency());
        var comments = await context.Comments.OrderBy(c => c.Id).ToListAsync(Ct);

        await using (var other = _db.Context())
        {
            (await other.Comments.SingleAsync(c => c.Id == 2, Ct)).Version = 1;
            await other.SaveChangesAsync(Ct);
        }

        context.Comments.RemoveRange(comments);
        if (async)
        {
            await context.SaveChangesAsync(Ct);
        }
        else
        {
            context.SaveChanges();
        }

        context.Entry(comments[0]).State.ShouldBe(EntityState.Detached);
        comments[0].DeletedAt.ShouldNotBeNull();

        await using var fresh = _db.Context();
        (await fresh.Comments.IncludeDeleted().SingleAsync(c => c.Id == 1, Ct)).DeletedAt.ShouldNotBeNull();
    }

    private sealed class SuppressConcurrency : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult ThrowingConcurrencyException(
            Microsoft.EntityFrameworkCore.Diagnostics.ConcurrencyExceptionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result)
            => Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult.Suppress();

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> ThrowingConcurrencyExceptionAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.ConcurrencyExceptionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult.Suppress());
    }

    [Fact]
    public async Task A_HardDelete_mark_from_one_pooled_lease_does_not_reach_the_next()
    {
        // The lease that called HardDelete() was returned without saving. Its scoped state lived on,
        // so the next lease's ordinary Remove() of the same instance deleted the row for real.
        var factory = PooledComments();
        Comment comment;
        await using (var first = factory.CreateDbContext())
        {
            comment = await first.Comments.SingleAsync(c => c.Id == 2, Ct);
            first.HardDelete(comment);
        }

        await using (var second = factory.CreateDbContext())
        {
            second.Comments.Remove(comment);
            await second.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Comments.IncludeDeleted().SingleAsync(c => c.Id == 2, Ct)).DeletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Clearing_an_optional_complex_value_before_Remove_is_discarded()
    {
        await using (var seed = _db.Context())
        {
            (await seed.Comments.SingleAsync(c => c.Id == 2, Ct)).Extra = new CommentMeta { Mood = "extra" };
            await seed.SaveChangesAsync(Ct);
        }

        await using (var context = _db.Context())
        {
            var comment = await context.Comments.SingleAsync(c => c.Id == 2, Ct);
            comment.Extra = null;
            context.Comments.Remove(comment);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        var stored = await fresh.Comments.IncludeDeleted().SingleAsync(c => c.Id == 2, Ct);
        stored.DeletedAt.ShouldNotBeNull();
        stored.Extra.ShouldNotBeNull().Mood.ShouldBe("extra");
    }

    [Fact]
    public async Task An_element_added_to_an_empty_complex_collection_before_Remove_is_discarded()
    {
        await using (var context = _db.Context())
        {
            var comment = await context.Comments.SingleAsync(c => c.Id == 2, Ct);
            comment.Reactions.Add(new CommentMeta { Mood = "added before the remove" });
            context.Comments.Remove(comment);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        var stored = await fresh.Comments.IncludeDeleted().SingleAsync(c => c.Id == 2, Ct);
        stored.DeletedAt.ShouldNotBeNull();
        stored.Reactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_complex_collection_changed_before_Remove_is_refused_plainly_and_nothing_is_written()
    {
        // EF Core cannot take the deleted entry back to Unchanged once a collection that already had
        // elements has changed, and has no way to restore it. The save fails before anything is sent —
        // with a message that says why.
        await using (var seed = _db.Context())
        {
            (await seed.Comments.SingleAsync(c => c.Id == 2, Ct)).Reactions.Add(new CommentMeta { Mood = "like" });
            await seed.SaveChangesAsync(Ct);
        }

        await using (var context = _db.Context())
        {
            var comment = await context.Comments.SingleAsync(c => c.Id == 2, Ct);
            comment.Reactions.Add(new CommentMeta { Mood = "added before the remove" });
            context.Comments.Remove(comment);

            (await Should.ThrowAsync<InvalidOperationException>(() => context.SaveChangesAsync(Ct)))
                .Message.ShouldContain("a complex collection on it (Reactions) was changed after it was loaded");
        }

        await using var fresh = _db.Context();
        var stored = await fresh.Comments.SingleAsync(c => c.Id == 2, Ct);
        stored.DeletedAt.ShouldBeNull();
        stored.Reactions.Select(r => r.Mood).ShouldBe(["like"]);
    }

    [Fact]
    public async Task Edits_to_a_table_split_owned_entity_before_Remove_are_discarded()
    {
        await using (var context = _db.Context())
        {
            var post = await context.Posts.SingleAsync(p => p.Id == 2, Ct);
            post.Seo.Slug = "edited before the remove";
            context.Posts.Remove(post);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        var stored = await fresh.Posts.IncludeDeleted().SingleAsync(p => p.Id == 2, Ct);
        stored.IsDeleted.ShouldBeTrue();
        stored.Seo.Slug.ShouldBe("two-slug");
    }

    [Fact]
    public async Task Edits_to_a_complex_property_before_Remove_are_discarded_like_any_other()
    {
        await using (var context = _db.Context())
        {
            var comment = await context.Comments.SingleAsync(c => c.Id == 2, Ct);
            comment.Meta.Mood = "edited before the remove";
            context.Comments.Remove(comment);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        var stored = await fresh.Comments.IncludeDeleted().SingleAsync(c => c.Id == 2, Ct);
        stored.DeletedAt.ShouldNotBeNull();
        stored.Meta.Mood.ShouldBe("calm");
    }

    private Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<PooledCommentsContext> PooledComments()
        => new(
            new DbContextOptionsBuilder<PooledCommentsContext>()
                .UseSqlite(_db.Connection)
                .UseEntityConventions(c => c.Clock(_db.Clock))
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            poolSize: 1);

    [Fact]
    public async Task A_pooled_context_still_restores_a_lost_concurrency_race_on_its_second_lease()
    {
        // EF puts a pooled context's event handlers back on every lease; the interceptor, scoped to the
        // context, lives on. Subscribing only once left the second lease with no restore at all.
        var options = new DbContextOptionsBuilder<PooledCommentsContext>()
            .UseSqlite(_db.Connection)
            .UseEntityConventions(c => c.Clock(_db.Clock))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        var factory = new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<PooledCommentsContext>(options, poolSize: 1);

        await using (var first = factory.CreateDbContext())
        {
            (await first.Comments.SingleAsync(c => c.Id == 1, Ct)).Text = "edited";
            await first.SaveChangesAsync(Ct);
        }

        await using var second = factory.CreateDbContext();
        var comment = await second.Comments.SingleAsync(c => c.Id == 2, Ct);
        await using (var other = _db.Context())
        {
            (await other.Comments.SingleAsync(c => c.Id == 2, Ct)).Version = 1;
            await other.SaveChangesAsync(Ct);
        }

        second.Comments.Remove(comment);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));

        second.Entry(comment).State.ShouldBe(EntityState.Deleted);
        comment.DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task After_a_failed_save_a_restored_principal_lets_its_cascaded_dependents_be_soft_deleted()
    {
        // The cascaded comments were marked for a hard delete in a save that failed. Kept, the marks
        // made the retry delete them for real even though their post was restored.
        await using var context = _db.Context();
        var post = await context.Posts.Include(p => p.Comments).SingleAsync(p => p.Id == 1, Ct);
        context.HardDelete(post);
        var duplicate = new Post { Id = 50, Title = "two", Tenant = 1 };   // unique index violation
        context.Posts.Add(duplicate);

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));

        context.Entry(duplicate).State = EntityState.Detached;
        context.Restore(post);
        await context.SaveChangesAsync(Ct);

        await using var fresh = _db.Context();
        (await fresh.Posts.AnyAsync(p => p.Id == 1, Ct)).ShouldBeTrue();
        var comments = await fresh.Comments.IncludeDeleted().ToListAsync(Ct);
        comments.Count.ShouldBe(2);
        comments.ShouldAllBe(c => c.DeletedAt != null);
    }

    [Fact]
    public async Task Restore_after_an_unaccepted_soft_delete_wins_over_replaying_it()
    {
        await using var context = _db.Context();
        var comment = await context.Comments.SingleAsync(c => c.Id == 2, Ct);
        context.Comments.Remove(comment);

        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);
            await transaction.RollbackAsync(Ct);
        }

        context.Restore(comment);
        await context.SaveChangesAsync(Ct);

        context.Entry(comment).State.ShouldBe(EntityState.Unchanged);
        await using var fresh = _db.Context();
        (await fresh.Comments.SingleAsync(c => c.Id == 2, Ct)).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_soft_delete_saved_without_accepting_is_still_a_soft_delete_on_the_retry()
    {
        // SaveChanges(false), roll back, save again: the retry used to send a plain update and leave the
        // entity tracked, where Find could still return it.
        await using var context = _db.Context();
        var comment = await context.Comments.SingleAsync(c => c.Id == 2, Ct);
        context.Comments.Remove(comment);

        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);
            await transaction.RollbackAsync(Ct);
        }

        _db.Clock.Now = BlogDatabase.Start.AddHours(1);
        await context.SaveChangesAsync(Ct);

        context.Entry(comment).State.ShouldBe(EntityState.Detached);
        await using var fresh = _db.Context();
        (await fresh.Comments.IncludeDeleted().SingleAsync(c => c.Id == 2, Ct)).DeletedAt
            .ShouldBe(BlogDatabase.Start.AddHours(1).UtcDateTime);
    }

    [Fact]
    public async Task HardDelete_deletes_the_dependents_its_cascade_reaches_for_real_too()
    {
        // Comments are soft-deletable, but cascaded from a hard-deleted post. Converted to updates, their
        // rows stayed and — with ClientCascade, no cascade in the database — the post's DELETE failed.
        await using (var context = _db.Context())
        {
            var post = await context.Posts.Include(p => p.Comments).SingleAsync(p => p.Id == 1, Ct);
            context.HardDelete(post);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = _db.Context();
        (await fresh.Posts.IncludeDeleted().AnyAsync(p => p.Id == 1, Ct)).ShouldBeFalse();
        (await fresh.Comments.IncludeDeleted().AnyAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_concurrency_conflict_leaves_the_delete_as_the_application_made_it_and_a_retry_converts_it_again()
    {
        // EF reports a concurrency conflict through its own hook, not SaveChangesFailed.
        await using var context = _db.Context();
        var comment = await context.Comments.SingleAsync(c => c.Id == 2, Ct);

        await using (var other = _db.Context())
        {
            (await other.Comments.SingleAsync(c => c.Id == 2, Ct)).Version = 1;
            await other.SaveChangesAsync(Ct);
        }

        context.Comments.Remove(comment);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync(Ct));

        var entry = context.Entry(comment);
        entry.State.ShouldBe(EntityState.Deleted);
        comment.DeletedAt.ShouldBeNull();

        // The usual resolution: take the database's token, and save again.
        entry.OriginalValues.SetValues((await entry.GetDatabaseValuesAsync(Ct))!);
        await context.SaveChangesAsync(Ct);

        entry.State.ShouldBe(EntityState.Detached);
        await using var fresh = _db.Context();
        (await fresh.Comments.IncludeDeleted().SingleAsync(c => c.Id == 2, Ct)).DeletedAt.ShouldNotBeNull();
    }

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
