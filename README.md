# EF.Toolkit

Extensions for [EF Core](https://learn.microsoft.com/ef/core) that do the things applications keep
writing by hand — fast bulk writes, and a real audit trail.

Every package **extends** EF Core from the outside. None is a fork, and none bundles EF: you install
it alongside whatever EF Core version your app already uses.

> **Status: pre-release.** Under active development toward `10.0.0`.

---

## Packages

| Package | What it does | Docs |
| --- | --- | --- |
| `EF.Toolkit.Bulk` | Makes `SaveChanges()` faster with no call-site changes, and adds an explicit `BulkInsert` / `Update` / `Delete` / `Merge` / `Synchronize` API — up to **10x faster and 44x less memory**. | [docs/bulk.md](docs/bulk.md) |
| `EF.Toolkit.Audit` | Records who changed what and when, with old and new values per column, into one audit table whose payload is queryable `jsonb`. | [docs/audit.md](docs/audit.md) |
| `EF.Toolkit.Audit.Bulk` | Joins the two: audits the explicit bulk API, and writes audit entries in bulk. | [docs/audit.md#with-eftoolkitbulk](docs/audit.md#with-eftoolkitbulk) |

Each capability has a provider package — `.PostgreSQL` or `.SqlServer` — which is the one you
install. The bridge is provider-neutral.

```bash
dotnet add package EF.Toolkit.Bulk.PostgreSQL
dotnet add package EF.Toolkit.Audit.PostgreSQL
dotnet add package EF.Toolkit.Audit.Bulk       # only if you use both
```

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
    .UseBulkAuditing());          // from EF.Toolkit.Audit.Bulk
```

If you reference both provider packages for a capability, the un-prefixed name is ambiguous — use
`UseNpgsqlBulk()` / `UseSqlServerBulk()` and `UseNpgsqlAuditing()` / `UseSqlServerAuditing()`.

---

## They are independent

`EF.Toolkit.Bulk` and `EF.Toolkit.Audit` reference neither each other nor anything of each other's.
Install one, the other, or both.

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
the explicit bulk API — and requires the resulting audit entries to be byte-identical. Both suites
carry negative controls that deliberately diverge and must be reported.

Everything runs against PostgreSQL 16, PostgreSQL 17 and SQL Server 2022.

```bash
dotnet test tests/EF.Toolkit.Bulk.Tests
dotnet test tests/EF.Toolkit.Audit.Tests

# Docker required
dotnet test tests/EF.Toolkit.Bulk.Equivalence
dotnet test tests/EF.Toolkit.Audit.Equivalence
```

---

## Samples

Both start their own PostgreSQL in Docker, so they need no setup:

```bash
dotnet run --project samples/EF.Toolkit.Bulk.Sample
dotnet run --project samples/EF.Toolkit.Audit.Sample
```

## License

MIT
