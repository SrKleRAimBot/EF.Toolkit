# EF.Toolkit.Audit.MySql

The MySQL provider for [EF.Toolkit.Audit](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md).
Install this package rather than the core one: it brings the core with it and maps the audit table
onto MySQL types.

Like EF.Toolkit.Bulk.MySql it references no MySQL provider, so it does not tie the application to
one; the differential harness runs it under Oracle's `MySql.EntityFrameworkCore`.

## Install

```bash
dotnet add package EF.Toolkit.Audit.MySql
```

## Setup

One call, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseMySQL(connectionString)
    .UseAuditing());
```

If you reference more than one provider package, use `UseMySqlAuditing()`.

## What this package provides

The audit table's shape is decided here:

| Column | Type |
| --- | --- |
| `EntityType`, `EntityKey`, `ActorId`, `ActorName`, `ActorType`, `TenantId`, `Source` | `varchar(256)` |
| `OccurredAt` | `datetime(6)` |
| `Changes` | `json` |

`json` is validated and parsed on the way in, so the payload needs no check constraint. `datetime(6)`
because MySQL has no type that keeps an offset: the instant is stored to the microsecond, and
without the precision two changes in the same second would lose their order.

### The table's schema

On the other engines the table goes in an `audit` schema by default. On MySQL a schema *is* a
database — one the application's account may not be allowed to create, and its backups may not
include — so here the table stays in the context's own database unless you say otherwise:

```csharp
.UseMySqlAuditing(a => a.Schema("audit_history"))
```

### Indexing the payload

MySQL has no index over a whole JSON document. Make a specific path searchable with a stored
generated column:

```csharp
.UseMySqlAuditing(a => a.IndexJsonPath("$.new.Status"))
```

The value is unquoted — `Shipped`, not `"Shipped"` — so an equality filter on the column matches
what you would expect. Name the paths a query actually filters on; each costs a column on every row.

Beyond that, entries are indexed by default on `(EntityType, EntityKey, OccurredAt DESC)` — the
history of one row — plus actor, correlation and tenant when multi-tenancy is configured.

## Supported versions

MySQL 8.4, covered by the differential harness on every commit. MySQL 8.0 and MariaDB are likely to
work but are not tested — MariaDB's `JSON` is an alias for `LONGTEXT` with a validity check, so the
payload behaves the same way there.

## Full documentation

Everything else — registering entity types and properties, actors, ambient scopes, multi-tenancy,
the payload format, options, diagnostics and limitations — is in the
[EF.Toolkit.Audit documentation](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md).

## License

MIT
