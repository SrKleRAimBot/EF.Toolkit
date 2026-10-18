# EF.Toolkit

Extensions for [EF Core](https://learn.microsoft.com/ef/core) that do the things applications keep
writing by hand — fast bulk writes, a real audit trail, and pagination that is actually correct.

Every package **extends** EF Core from the outside. None is a fork, and none bundles EF: you install
it alongside whatever EF Core version your app already uses.

> **Status: pre-release.** Under active development toward `10.0.0`.

---

## Packages

| Package | What it does | Docs |
| --- | --- | --- |
| `EF.Toolkit.Bulk` | Makes `SaveChanges()` faster with no call-site changes, and adds an explicit `BulkInsert` / `Update` / `Delete` / `Merge` / `Synchronize` API — up to **10x faster and 44x less memory**. | [docs/bulk.md](docs/bulk.md) |
| `EF.Toolkit.Audit` | Records who changed what and when, with old and new values per column, into one audit table whose payload is queryable JSON — `jsonb` on PostgreSQL. | [docs/audit.md](docs/audit.md) |
| `EF.Toolkit.Audit.Bulk` | Joins the two: audits the explicit bulk API, and writes audit entries in bulk. | [docs/audit-bulk.md](docs/audit-bulk.md) |
| `EF.Toolkit.Query` | Offset and keyset pagination, sorting contracts with a guaranteed total order, composable filters, batched streaming and ambient tracking scopes — plus a development-time advisor for missing indexes. | [docs/query.md](docs/query.md) |

The write-side capabilities have a provider package — `.PostgreSQL`, `.SqlServer`, `.MySql` or
`.Sqlite` — which is the one you install. The bridge and `EF.Toolkit.Query` are provider-neutral.

```bash
dotnet add package EF.Toolkit.Bulk.PostgreSQL
dotnet add package EF.Toolkit.Audit.PostgreSQL
dotnet add package EF.Toolkit.Audit.Bulk       # only if you use both
dotnet add package EF.Toolkit.Query
```

Each provider package has its own notes on how the capability is executed on that engine, and what
differs between them:

| | Bulk | Audit |
| --- | --- | --- |
| PostgreSQL | [bulk on PostgreSQL](docs/bulk-postgresql.md) | [audit on PostgreSQL](docs/audit-postgresql.md) |
| SQL Server | [bulk on SQL Server](docs/bulk-sqlserver.md) | [audit on SQL Server](docs/audit-sqlserver.md) |
| MySQL | [bulk on MySQL](docs/bulk-mysql.md) | [audit on MySQL](docs/audit-mysql.md) |
| SQLite | [bulk on SQLite](docs/bulk-sqlite.md) | [audit on SQLite](docs/audit-sqlite.md) |

The MySQL packages reference no MySQL provider, so they do not tie an application to one; they are
tested under Oracle's `MySql.EntityFrameworkCore`, the one that ships for EF Core 10.

| Version | EF Core | TFM |
| --- | --- | --- |
| `10.x` | 10.x | `net10.0` |
| `9.x` | 9.x | `net8.0` |

The version tracks EF Core's, because these packages hook low-level services and must never resolve
across an EF major.

---

## Setup

One call per capability, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseNpgsql(connectionString)
    .UseBulkOperations()
    .UseAuditing(a => a.Schema("audit"))
    .UseBulkAuditing()            // from EF.Toolkit.Audit.Bulk
    .UseQueryHelpers());
```

If you reference more than one provider package for a capability, the un-prefixed name is
ambiguous — use the prefixed aliases: `UseNpgsqlBulk()`, `UseSqlServerBulk()`, `UseMySqlBulk()`,
`UseSqliteBulk()`, and likewise `UseNpgsqlAuditing()` through `UseSqliteAuditing()`.

---

## They are independent

`EF.Toolkit.Bulk`, `EF.Toolkit.Audit` and `EF.Toolkit.Query` reference neither each other nor anything
of each other's. Install one, some, or all.

What each exposes for the other to build on is public API worth having on its own:

- **`IBulkWriteObserver`** sees the entities, values and before-images of an explicit bulk operation,
  inside its transaction — for an outbox, a cache invalidation, a projection, or an audit trail.
- **`IAuditEntryFactory`** turns a described change into audit entries, so any capture path produces
  entries identical to the ones the change tracker produces.
- **`IAuditBatchWriter`** is an optional faster path for writing many entries at once.

`EF.Toolkit.Audit.Bulk` is a small package that implements the first in terms of the second, and the
third in terms of `BulkInsertAsync`. That is the whole of the integration.

---

## Correctness

The primary gate for both capabilities is a differential harness against real engines.

`EF.Toolkit.Bulk` runs every scenario twice — once through stock EF, once through the library — and
compares raw table contents, full change-tracker state and failure behaviour.

`EF.Toolkit.Audit` runs the same logical change twice — once through `SaveChanges()`, once through
the explicit bulk API — and requires the resulting audit entries to be byte-identical.

`EF.Toolkit.Query` walks a whole result set three ways — forward by keyset, backward by keyset, and
page by page by offset — and requires all three to cover every row exactly once, in the ordering's own
order. All three suites carry negative controls that deliberately diverge and must be reported.

Everything runs against PostgreSQL 16, PostgreSQL 17, SQL Server 2022, MySQL 8.4 and SQLite. SQLite
needs no container; filter to it with `--filter Engine=sqlite` to run an equivalence suite without
Docker.

```bash
dotnet test tests/EF.Toolkit.Bulk.Tests
dotnet test tests/EF.Toolkit.Audit.Tests
dotnet test tests/EF.Toolkit.Query.Tests

# Docker required
dotnet test tests/EF.Toolkit.Bulk.Equivalence
dotnet test tests/EF.Toolkit.Audit.Equivalence
dotnet test tests/EF.Toolkit.Query.Equivalence
```

---

## Samples

Each starts its own PostgreSQL in Docker, so they need no setup:

```bash
dotnet run --project samples/EF.Toolkit.Bulk.Sample
dotnet run --project samples/EF.Toolkit.Audit.Sample
dotnet run --project samples/EF.Toolkit.Query.Sample
```

## License

MIT
