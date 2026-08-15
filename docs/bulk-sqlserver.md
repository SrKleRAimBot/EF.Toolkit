# EF.Toolkit.Bulk.SqlServer

The SQL Server provider for [EF.Toolkit.Bulk](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk.md).
Install this package rather than the core one: it brings the core with it and supplies the executor
that does the actual work on SQL Server.

## Install

```bash
dotnet add package EF.Toolkit.Bulk.SqlServer
```

## Setup

One call, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseSqlServer(connectionString)
    .UseBulkOperations());
```

If you reference *both* provider packages, `UseBulkOperations` is ambiguous — use `UseSqlServerBulk()`.

## What this package provides

Everything in the core is provider-neutral until a batch has been grouped by
`(table, state, column shape)`. This package is what that batch is handed to:

| | How it runs on SQL Server |
| --- | --- |
| Insert | `SqlBulkCopy` |
| Generated keys | staging table + `MERGE … OUTPUT` |
| Update / delete | staging table + `UPDATE … FROM` / `DELETE … FROM`, with `OUTPUT` |
| Upsert | `MERGE` with `$action` |
| Synchronize | `WHEN NOT MATCHED BY SOURCE THEN DELETE` |

Update, delete and merge carry a **source ordinal** through the staging table, so a returned row maps
back to the row that produced it by position rather than by matching key values. SQL Server's
`OUTPUT` may name a table from the statement's `FROM` clause, so the ordinal comes back directly.

Unlike PostgreSQL, SQL Server has no sequence behind an `IDENTITY` column, so this provider stages
with an ordinal on every version rather than reserving keys up front.

Staging tables are indexed on their join columns above `StagingIndexThreshold` rows.

## Supported versions

SQL Server 2022, covered by the differential harness on every commit. 2019 and Azure SQL are likely
to work — nothing here depends on a 2022-only feature — but are not tested.

## Full documentation

Everything else — transparent versus explicit mode, the explicit API, change tracking, options,
diagnostics, measured performance and limitations — is in the
[EF.Toolkit.Bulk documentation](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk.md).

## License

MIT
