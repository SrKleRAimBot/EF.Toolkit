# EF.Toolkit.Bulk.MySql

The MySQL provider for [EF.Toolkit.Bulk](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk.md).
Install this package rather than the core one: it brings the core with it and supplies the executor
that does the actual work on MySQL.

It references no MySQL provider or driver. It runs under Oracle's `MySql.EntityFrameworkCore` — the
provider that ships for EF Core 10, and the one the differential harness runs against — and is
written to plain ADO.NET so that it is not tied to it.

## Install

```bash
dotnet add package EF.Toolkit.Bulk.MySql
```

## Setup

One call, *after* the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseMySQL(connectionString)
    .UseBulkOperations());
```

The order is not a style preference here. This package extends the batch factory the MySQL
provider registers, so the provider has to have registered it first; calling `UseBulkOperations()`
before `UseMySQL()` throws as soon as the context is built, rather than silently leaving
`SaveChanges` unaccelerated.

If you reference more than one provider package, `UseBulkOperations` is ambiguous — use
`UseMySqlBulk()`.

## What this package provides

| | How it runs on MySQL |
| --- | --- |
| Insert | multi-row `INSERT … VALUES`, up to 1,000 rows a statement |
| Generated keys | `LAST_INSERT_ID()` plus the statement's consecutive `AUTO_INCREMENT` run — see below |
| Update / delete | temporary staging table + `UPDATE … JOIN` / `DELETE … JOIN`, located first by `SELECT … FOR UPDATE` |
| Upsert | staging table, `SELECT … FOR UPDATE` to find what exists, `UPDATE … JOIN` for those, multi-row `INSERT` for the rest |
| Synchronize | the upsert, plus `DELETE … WHERE NOT EXISTS` against the staging table |

### Why not `LOAD DATA`

MySQL's bulk-load statement, `LOAD DATA LOCAL INFILE`, is off by default on the server and on every
driver, for good security reasons, and a library cannot ask an application to turn it on. Multi-row
`INSERT` needs nothing enabled, and it is where nearly all of the gain is anyway: one statement
parsed, planned and acknowledged per thousand rows rather than one per row. Statements are bounded
by row count, parameter count and an estimate of their size, so wide or text-heavy rows stay well
inside `max_allowed_packet`.

### Generated keys and `innodb_autoinc_lock_mode`

MySQL has no `RETURNING` or `OUTPUT`, so nothing a statement generates comes back with it.
`LAST_INSERT_ID()` reports the key given to a multi-row insert's *first* row, and under InnoDB's
"traditional" (0) and "consecutive" (1) lock modes MySQL guarantees the rest follow it, a step of
`auto_increment_increment` apart. Both are checked, not assumed, so every row's key is exact.

MySQL 8's default is "interleaved" (2), which makes no such guarantee. There each row is inserted by
a statement of its own followed by its own `LAST_INSERT_ID()` — still exact, still a chunk of rows
to a round trip, but the server parses every row. Where inserts are hot, set:

```ini
[mysqld]
innodb_autoinc_lock_mode = 1
```

Rows that bring their own keys, and every update, delete and merge, are unaffected by the setting.
The key column is also checked to really be `AUTO_INCREMENT` — a key filled by a trigger or a
default expression is just as "generated" to EF, and `LAST_INSERT_ID()` knows nothing about it, so
such an insert declines to stock EF Core.

An insert that needs any *other* generated value back — a default, a computed column — declines to
stock EF Core too, because MySQL has no way to report it. A merge does not: it finds every row it
wrote again through its match columns, and reads whatever it needs from there.

### Why the upsert is not `ON DUPLICATE KEY UPDATE`

`INSERT … ON DUPLICATE KEY UPDATE` fires on a clash with *any* unique index or the primary key, not
with the columns you chose to match on — a merge by email would update whichever row's surrogate key
happened to collide — and it cannot say which rows it inserted and which it updated. Finding the
existing rows first with a locking read answers all of that: the insert-versus-update split is exact,
and under InnoDB's default isolation the read's gap locks keep a concurrent session from inserting a
matching row in between. Index the match columns; without an index that protection becomes a lock
over the whole scan, which is still correct but contended.

A merge whose source holds two rows with the same match values is refused before anything is
written, as the engines with a `MERGE` statement refuse it.

### Staging tables

Staging tables are `TEMPORARY`, which matters more on MySQL than anywhere else: creating and dropping
a temporary table does not end the transaction, but almost every other DDL statement does, and an
implicit commit halfway through a bulk operation is the one thing it must never do. For the same
reason a staging index is declared when the table is created rather than added after the load.
Columns of `TEXT`, `BLOB` or `JSON` type are never indexed — MySQL can only index them by prefix.

With `enforce_gtid_consistency` on, MySQL before 8.0.13 rejects a temporary table inside a
transaction. From 8.0.13 it allows one under row-based or mixed binary logging, and row-based is the
default.

### Differences from stock EF Core

Oracle's provider words its `DbUpdateException` as "Could not save changes. Please configure your
entity type accordingly." EF.Toolkit.Bulk wraps store failures in EF Core's own wording, as every
other provider does. The exception types, and the inner `MySqlException` with its error number, are
the same either way.

## Supported versions

MySQL 8.4, covered by the differential harness on every commit under both lock modes 1 and 2.
MySQL 8.0 and MariaDB are likely to work — nothing here goes beyond what both have — but are not
tested.

## Full documentation

Everything else — transparent versus explicit mode, the explicit API, change tracking, options,
diagnostics, measured performance and limitations — is in the
[EF.Toolkit.Bulk documentation](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk.md).

## License

MIT
