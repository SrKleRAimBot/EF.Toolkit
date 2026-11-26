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
made to the entity before it was removed are discarded, exactly as a real delete would discard them.

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

`Restore()` after `Remove()` in the same unit of work simply cancels it.

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
principal being soft-deleted is restored.

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
