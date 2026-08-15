# EF.Toolkit.Bulk.PostgreSQL

The PostgreSQL provider for [EF.Toolkit.Bulk](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk.md).
Install this package rather than the core one: it brings the core with it and supplies the executor
that does the actual work on Npgsql.

## Install

```bash
dotnet add package EF.Toolkit.Bulk.PostgreSQL
```

## Setup

One call, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseNpgsql(connectionString)
    .UseBulkOperations());
```

If you reference *both* provider packages, `UseBulkOperations` is ambiguous — use `UseNpgsqlBulk()`.

## What this package provides

Everything in the core is provider-neutral until a batch has been grouped by
`(table, state, column shape)`. This package is what that batch is handed to:

| | How it runs on PostgreSQL |
| --- | --- |
| Insert | binary `COPY` |
| Generated keys | sequence values reserved up front (below 17) |
| Update / delete | staging table + `UPDATE … FROM` / `DELETE … USING`, with `RETURNING` |
| Upsert | `MERGE … RETURNING merge_action()` on 17+, else `INSERT … ON CONFLICT … DO UPDATE` |
| Synchronize | `WHEN NOT MATCHED BY SOURCE` on 17+, else a second `DELETE … WHERE NOT EXISTS` |

### Why the version matters

`INSERT … ON CONFLICT … RETURNING` can only name columns of the *target* row, never the source, so
before PostgreSQL 17 there is no documented way to map a staged insert's generated keys back to the
rows that produced them. That is why this provider reserves sequence values up front instead.

PostgreSQL 17's `MERGE … RETURNING` *can* see the source. On 17 and later neither the reservation
nor the insert-versus-update counting workaround is needed, and the provider selects the better path
automatically — there is nothing to configure.

Staging tables are analysed before they are joined, because a freshly loaded temporary table has no
statistics and autovacuum never touches one on PostgreSQL. Without that the planner joins it to the
target on a guess.

## Supported versions

PostgreSQL 16 and 17, both covered by the differential harness on every commit. Earlier versions are
likely to work but are not tested.

## Full documentation

Everything else — transparent versus explicit mode, the explicit API, change tracking, options,
diagnostics, measured performance and limitations — is in the
[EF.Toolkit.Bulk documentation](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk.md).

## License

MIT
