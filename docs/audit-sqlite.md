# EF.Toolkit.Audit.Sqlite

The SQLite provider for [EF.Toolkit.Audit](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md).
Install this package rather than the core one: it brings the core with it and maps the audit table
onto SQLite.

## Install

```bash
dotnet add package EF.Toolkit.Audit.Sqlite
```

## Setup

One call, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseSqlite(connectionString)
    .UseAuditing());
```

If you reference more than one provider package, use `UseSqliteAuditing()`.

## What this package provides

| Column | Type |
| --- | --- |
| `EntityType`, `EntityKey`, `ActorId`, `ActorName`, `ActorType`, `TenantId`, `Source` | `TEXT` |
| `OccurredAt` | `TEXT`, EF Core's own `DateTimeOffset` format |
| `Changes` | `TEXT` with `CHECK (json_valid(…))` |

SQLite stores JSON as text and does not validate it, so the check earns its keep: a payload that is
not JSON would be unreadable by every `json_extract` anyone later writes against it.

SQLite has no schemas — EF Core ignores one, with a warning — so the `audit` schema the other engines
default to is dropped here.

### Indexing the payload

```csharp
.UseSqliteAuditing(a => a.IndexJsonPath("$.new.Status"))
```

adds a stored generated column over `json_extract("Changes", '$.new.Status')`, with an index on it.
SQLite cannot add a stored generated column to an existing table, so a path added later needs the
table rebuilt — which EF Core's SQLite migrations do for you.

### Limits that come from SQLite

- EF Core cannot `ORDER BY` a `DateTimeOffset` on SQLite, so a query ordering entries by
  `OccurredAt` has to order on the client. The index over it still serves filters.
- EF Core's SQLite provider does not take part in `System.Transactions` scopes, so audit entries
  cannot either.

## Supported versions

SQLite 3.35 and later, through `Microsoft.Data.Sqlite`, covered by the differential harness on
every commit.

## Full documentation

Everything else — registering entity types and properties, actors, ambient scopes, multi-tenancy,
the payload format, options, diagnostics and limitations — is in the
[EF.Toolkit.Audit documentation](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md).

## License

MIT
