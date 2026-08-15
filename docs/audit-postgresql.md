# EF.Toolkit.Audit.PostgreSQL

The PostgreSQL provider for [EF.Toolkit.Audit](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md).
Install this package rather than the core one: it brings the core with it and maps the audit table
onto PostgreSQL types.

## Install

```bash
dotnet add package EF.Toolkit.Audit.PostgreSQL
```

## Setup

One call, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseNpgsql(connectionString)
    .UseAuditing(a => a.Schema("audit")));
```

If you reference both provider packages, use `UseNpgsqlAuditing()`.

## What this package provides

The audit table's shape is decided here. The columns that differ from SQL Server:

| Column | Type |
| --- | --- |
| `EntityType`, `EntityKey`, `ActorId`, `ActorName`, `ActorType`, `TenantId`, `Source` | `text` |
| `OccurredAt` | `timestamptz` |
| `CorrelationId` | `uuid` |
| `Changes` | **`jsonb`**, behind a GIN index |

### The payload is searchable, not just readable

`jsonb` under a GIN index is the reason to prefer PostgreSQL for an audit trail: the payload answers
containment queries directly, so a trail can be searched by *what actually changed* rather than only
read back per row.

```sql
-- uses the GIN index
SELECT * FROM audit."AuditEntries"
WHERE "Changes" @> '{"new":{"Status":"Shipped"}}';

-- also indexed
SELECT * FROM audit."AuditEntries"
WHERE "Changes" -> 'changed' ? 'Status';
```

`old` and `new` are sibling objects rather than a per-property pair of the two, precisely because
that is the shape a containment index can answer.

Beyond the payload index, entries are indexed by default on
`(EntityType, EntityKey, OccurredAt DESC)` — the history of one row — plus actor, correlation and
tenant when multi-tenancy is configured.

## Supported versions

PostgreSQL 16 and 17, both covered by the differential harness on every commit.

## Full documentation

Everything else — registering entity types and properties, actors, ambient scopes, multi-tenancy,
the payload format, options, diagnostics and limitations — is in the
[EF.Toolkit.Audit documentation](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md).

## License

MIT
