# EF.Toolkit.Conventions

Entity conventions that [EF Core](https://learn.microsoft.com/ef/core) leaves every application to
write by hand: **soft delete** and **created/updated timestamps**.

EF.Toolkit.Conventions **extends** EF Core from the outside. It is not a fork, and it does not bundle
EF — you install it alongside whatever EF Core version your app already uses. It is provider-neutral.

- **Soft delete.** `Remove()` marks the row instead of deleting it, and a named query filter hides it
  from every query. Restore it, read past the filter, or delete it for real when you have to.
- **Timestamps.** Insert and last-write times maintained on every save, from a clock you can replace.

> **Status: pre-release.** Under active development toward `10.0.0`.

---

## Contents

- [Install](#install) · [Setup](#setup)
- [Soft delete](#soft-delete) — [reading deleted rows](#reading-deleted-rows) ·
  [restore and hard delete](#restore-and-hard-delete) · [set-based](#set-based) ·
  [what Remove() had already done](#what-remove-had-already-done)
- [Timestamps](#timestamps)
- [Options](#options) · [Limitations](#limitations)

---

## Install

```bash
dotnet add package EF.Toolkit.Conventions
```

## Setup

Two halves. The model says which property means what:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Post>(b =>
    {
        b.HasSoftDelete(p => p.DeletedAt);    // bool, DateTime? or DateTimeOffset?
        b.HasCreatedAt(p => p.CreatedAt);
        b.HasUpdatedAt(p => p.UpdatedAt);
    });

    modelBuilder.Entity<Note>()
        .HasSoftDelete()                       // shadow bool "IsDeleted"
        .HasTimestamps();                      // shadow DateTime "CreatedAt" / "UpdatedAt", UTC
}
```

and the options switch on the save-time behaviour:

```csharp
services.AddDbContext<BlogContext>(o => o
    .UseNpgsql(connectionString)
    .UseEntityConventions());
```

Without `UseEntityConventions()` the model still declares the query filter, so marked rows stay
hidden, but `Remove()` deletes and nothing is stamped.

---

## Soft delete

```csharp
db.Posts.Remove(post);
await db.SaveChangesAsync();
// UPDATE posts SET deleted_at = @now, updated_at = @now WHERE id = @id

await db.Posts.AnyAsync(p => p.Id == post.Id);   // false
db.Entry(post).State;                            // Detached, as after a real delete
```

Only the soft-delete column — and the updated-at timestamp, if the type has one — is written. Edits
made to the entity before it was removed are discarded, exactly as a real delete would discard them:
to its own columns, its complex properties (including one set to `null`), and owned entities kept in
its row.

One exception to that: an optional complex value whose members were **all at their defaults** (`0`,
`false`, `null`) and which was set to `null` before `Remove()`. EF keeps no record of whether an optional
complex value was present — an absent one reads back as all defaults too — so the two cannot be told
apart, and on EF Core 11 that clear is written with the soft delete. To keep such a value, leave it
assigned when calling `Remove()`.

On EF Core 10 one edit cannot be discarded: an element added to, or removed from, a **complex
collection** that already had elements. EF Core 10 cannot take the deleted entry back to unchanged in
that state and has no way to restore the collection, so the save is refused with a message naming the
collection, before anything is written — undo the collection change, or save it first. EF Core 11
handles it, and the change is discarded like the rest.

The property can be a `bool` (`true` means deleted) or a nullable `DateTime` / `DateTimeOffset` (a
value means deleted, and records when). A `DateTime` is always written in UTC.

Soft delete is configured on the **root** of an inheritance hierarchy, because that is where EF
allows a query filter, and it then applies to every derived type.

### Reading deleted rows

The filter is a [named query filter](https://learn.microsoft.com/ef/core/querying/filters), called
`SoftDeleteFilter.Name`, so it can be switched off on its own while a tenant filter on the same type
stays in force:

```csharp
db.Posts.IncludeDeleted()             // live and deleted, every other named filter still applied
db.Posts.OnlyDeleted()                // a recycle bin
db.IsSoftDeleted(post)                // by the values the instance carries
```

EF Core does not allow an unnamed query filter beside a named one, so give any other filter on a
soft-deletable type a name: `b.HasQueryFilter("Tenant", p => p.TenantId == tenantId)`.

### Restore and hard delete

```csharp
var post = await db.Posts.OnlyDeleted().SingleAsync(p => p.Id == id);
db.Restore(post);                      // attaches an untracked instance too
await db.SaveChangesAsync();

db.HardDelete(post);                   // a real DELETE — erasure requests, purges
await db.SaveChangesAsync();
```

`Restore()` after `Remove()` in the same unit of work cancels it: the entity, its owned data, and the
foreign keys `Remove()` nulled on loaded optional dependents are all put back. Dependents that
`Remove()` cascade-*deleted* (required relationships) are not revived, since nothing tells them apart
from ones the application removed itself; use `CascadeTiming.OnSaveChanges` (below) where that matters,
under which `Remove()` touches no dependent until the save.

The nulled keys are put back from the value EF tracks as each dependent's **original** — the one it
was loaded with. Two kinds of dependent have none to put back, and under `Immediate` timing keep the
`null` `Remove()` gave them, for both a soft delete and a `Restore()`:

- one moved to the removed principal earlier in the same unit of work — its key's value from just
  before `Remove()` is kept nowhere EF exposes;
- one **added** in the same unit of work — a new post given the author that is then removed is
  inserted with `AuthorId` `null`, since an added entry has no original value and `Remove()` clears its
  navigation too.

`OnSaveChanges` timing has no fixup to undo and keeps both.

### Set-based

The same, as one statement over every matching row, with nothing loaded:

```csharp
await db.Posts.SoftDeleteWhereAsync(p => p.BlogId == blogId);
await db.Posts.RestoreWhereAsync(p => p.DeletedAt > cutoff);
```

`SoftDeleteWhere` skips rows that are already deleted — the filter still applies — so a timestamp
keeps recording when a row was *first* deleted.

### What Remove() had already done

By the time a save starts, `Remove()` has already done EF's cascade work on the assumption that the
row is about to disappear. Since it is not, that work is undone where it would be wrong:

| What `Remove()` did to tracked entities | What the save does instead |
| --- | --- |
| Marked owned entities deleted (for a table-split owned type: nulled its columns) | Keeps them |
| Set optional dependents' foreign keys to `null` | Puts the keys back |
| Marked cascade-delete dependents deleted, and they are soft-deletable | Soft-deletes them too |
| Marked cascade-delete dependents deleted, and they are **not** soft-deletable | Deletes them, as asked |

A foreign key the application set to `null` itself is left alone — only one that pointed at a
principal being soft-deleted is restored. The one case that cannot be told apart is a key the
application nulled **and** whose principal it then removed in the same unit of work: under EF's
default `CascadeDeleteTiming.Immediate`, `Remove()`'s own fixup leaves exactly the same state, so that
key is restored too.

Set the context's cascade timing to `OnSaveChanges` to remove the ambiguity:

```csharp
context.ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
context.ChangeTracker.DeleteOrphansTiming = CascadeTiming.OnSaveChanges;
```

Then `Remove()` cascades nothing before the save, the soft delete turns the principal into an update
before EF's own cascade runs, and EF finds nothing to cascade. Every foreign key the application set
is kept exactly, owned data is kept, and loaded dependents are left as they are rather than
soft-deleted alongside.

---

## Timestamps

```csharp
b.HasCreatedAt(p => p.CreatedAt);      // DateTime or DateTimeOffset, optionally nullable
b.HasUpdatedAt(p => p.UpdatedAt);
```

| Save | `CreatedAt` | `UpdatedAt` |
| --- | --- | --- |
| Insert | set, unless the entity already carries a value | set, unless already carried |
| Update | **reverted** if the application changed it | set |
| Soft delete | untouched | set |

A value the application sets on insert — an import, a seed with fixed times — is kept. `CreatedAt`
records a fact about the past, so an update that changes it has the change reverted rather than
written.

Changes are detected before stamping, so an entity edited without change notification is still
stamped.

---

## Options

```csharp
.UseEntityConventions(c => c
    .Clock(timeProvider)        // any TimeProvider — a fake one in tests
    .WithoutSoftDelete()        // Remove() deletes; the filter still hides marked rows
    .WithoutTimestamps())
```

Calling `UseEntityConventions` again amends the earlier call rather than adding a second
interceptor.

---

## Limitations

- Set-based writes — EF's `ExecuteDelete`, and `DeleteWhere` from EF.Toolkit.Bulk — are real deletes.
  Use `SoftDeleteWhereAsync` for the soft equivalent. Likewise `ExecuteUpdate` stamps nothing.
- The explicit bulk API from EF.Toolkit.Bulk (`BulkDeleteAsync` and friends) bypasses `SaveChanges()`,
  and so these conventions.
- Dependents that are **not loaded** are not touched by a soft delete — the database never sees a
  `DELETE`, so its own `ON DELETE CASCADE` never fires. They stay, pointing at a row the filter now
  hides. Make them soft-deletable and load them, or soft-delete them with `SoftDeleteWhereAsync`.
- The clock is part of the options EF caches its internal service provider by. Pass one long-lived
  `TimeProvider`, not a new instance per context.
- A unique index still sees soft-deleted rows. If a deleted row's value must be reusable, make the
  index filtered (`HasFilter("deleted_at IS NULL")`) or include the soft-delete column in it.
- EF.Toolkit.Audit captures a save from its own `SaveChanges` interceptor. Combined with these
  conventions, whether it records a soft delete as a delete or as the update that reaches the database
  depends on which interceptor runs first; this combination is not yet covered by tests.

## License

MIT
