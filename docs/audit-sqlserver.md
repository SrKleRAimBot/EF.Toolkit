# EF.Toolkit.Audit.SqlServer

The SQL Server provider for [EF.Toolkit.Audit](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md).
Install this package rather than the core one: it brings the core with it and maps the audit table
onto SQL Server types.

## Install

```bash
dotnet add package EF.Toolkit.Audit.SqlServer
```

## Setup

One call, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseSqlServer(connectionString)
    .UseAuditing(a => a.Schema("audit")));
```

If you reference both provider packages, use `UseSqlServerAuditing()`.

## What this package provides

The audit table's shape is decided here. The columns that differ from PostgreSQL:

| Column | Type |
| --- | --- |
| `EntityType`, `EntityKey`, `ActorId`, `ActorName`, `ActorType`, `TenantId`, `Source` | `nvarchar(256)` |
| `OccurredAt` | `datetimeoffset(7)` |
| `CorrelationId` | `uniqueidentifier` |
| `Changes` | `nvarchar(max)` with `CHECK (ISJSON(…) = 1)` |

### Indexing the payload

SQL Server has no index over a JSON document, so unlike PostgreSQL's `jsonb`, the payload here is
validated but not searchable in general. Make a specific path searchable with a persisted computed
column:

```csharp
.UseSqlServerAuditing(a => a.IndexJsonPath("$.new.Status"))
```

That is a per-path decision rather than a blanket one — name the paths a query actually filters on.

Beyond that, entries are indexed by default on `(EntityType, EntityKey, OccurredAt DESC)` — the
history of one row — plus actor, correlation and tenant when multi-tenancy is configured.

## Supported versions

SQL Server 2022, covered by the differential harness on every commit. 2019 and Azure SQL are likely
to work — `ISJSON` and persisted computed columns predate 2022 — but are not tested.

## Full documentation

Everything else — registering entity types and properties, actors, ambient scopes, multi-tenancy,
the payload format, options, diagnostics and limitations — is in the
[EF.Toolkit.Audit documentation](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md).

## License

MIT
