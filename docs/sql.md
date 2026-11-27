# EF.Toolkit.Sql

The advanced SQL features EF Core leaves to raw strings — views, materialized views, stored
procedures, functions, row-level security and session context — as definitions that render the right
SQL for the engine you are on.

- **One definition, every engine.** A `ViewDefinition` renders `CREATE OR REPLACE VIEW` on PostgreSQL
  and MySQL, `CREATE OR ALTER VIEW` on SQL Server, and drop-then-create on SQLite. The same goes for
  procedures, functions and policies.
- **Migrations and runtime alike.** The same definitions go into a migration
  (`migrationBuilder.CreateView(...)`) and into a database built with `EnsureCreated`
  (`context.Database.CreateDatabaseObjectsAsync(...)`), so a test database cannot drift from production
  by having its views written twice.
- **Tenant isolation in one line.** `TenantIsolationPolicy` sets up row-level security on PostgreSQL or
  a security policy on SQL Server, failing closed when no tenant is set.
- **Session context on every connection open.** `UseSessionContext` applies `app.tenant_id` (or
  whatever the policies read) each time EF opens a connection, so pooling cannot quietly drop it.
- **Parameterised calls.** `ExecuteProcedureAsync`, `QueryProcedureAsync<T>` and a composable
  `QueryFunction<T>` bind every value as a parameter through EF's own raw-SQL pipeline.

**It refuses rather than guesses.** A feature an engine does not have — materialized views on SQL
Server, stored procedures on SQLite, row-level security on MySQL — throws
`SqlFeatureNotSupportedException` naming the feature and the engine. Nothing ever emits SQL that runs
but does something other than what was asked.

---

## Contents

[Install](#install) · [Setup](#setup) · [Views](#views) · [Materialized views](#materialized-views) ·
[Stored procedures](#stored-procedures) · [Functions](#functions) ·
[Row-level security](#row-level-security) — [tenant isolation](#tenant-isolation),
[PostgreSQL policies](#postgresql-policies), [SQL Server security policies](#sql-server-security-policies) ·
[Session context](#session-context) · [Databases without migrations](#databases-without-migrations) ·
[Per-engine support](#per-engine-support) · [How it works](#how-it-works) · [Limitations](#limitations)

---

## Install

```bash
dotnet add package EF.Toolkit.Sql
```

One package. It references only `Microsoft.EntityFrameworkCore.Relational` and picks the SQL dialect
from the provider already configured: `Npgsql.EntityFrameworkCore.PostgreSQL`,
`Microsoft.EntityFrameworkCore.SqlServer`, `MySql.EntityFrameworkCore`, `Pomelo.EntityFrameworkCore.MySql`
or `Microsoft.EntityFrameworkCore.Sqlite`. Any other provider is refused by name rather than guessed at.

## Setup

Nothing to configure for migrations and runtime calls — the extension methods live in EF's own
namespaces (`Microsoft.EntityFrameworkCore.Migrations` and `Microsoft.EntityFrameworkCore`), so they are
visible in every migration and every file that already uses EF. The definitions themselves are in
`EFToolkit.Sql.Definitions` and `EFToolkit.Sql.Security`.

Session context is the one feature with options-time setup; see [Session context](#session-context).

---

## Views

```csharp
public static class Schema
{
    public static readonly ViewDefinition ActiveCustomers = new(
        "ActiveCustomers",
        """SELECT "Id", "Name" FROM "Customers" WHERE "IsActive" """);
}

// In a migration
migrationBuilder.CreateView(Schema.ActiveCustomers);
migrationBuilder.DropView("ActiveCustomers");          // in Down()

// Or without a shared definition
migrationBuilder.CreateView("ActiveCustomers", "SELECT ...", schema: "sales");
```

Map it as a keyless entity and query it like any other:

```csharp
modelBuilder.Entity<ActiveCustomer>().HasNoKey().ToView("ActiveCustomers");
```

Redefining a view replaces it in place on PostgreSQL, SQL Server and MySQL, so grants on it survive.
SQLite has no replace, so there it is dropped and created again — SQLite has no grants to lose. The
query is yours and must be valid on the engine that runs it; trailing semicolons are dropped, before a trailing comment too, and the comment is left as written. Drops are
always `IF EXISTS`.

## Materialized views

PostgreSQL only.

```csharp
migrationBuilder.CreateMaterializedView(
    "order_totals",
    """SELECT "TenantId", sum("Total") AS "Total" FROM "Orders" GROUP BY "TenantId" """,
    withNoData: true);   // defer the query to the first refresh

await context.Database.RefreshMaterializedViewAsync("order_totals");
await context.Database.RefreshMaterializedViewAsync("order_totals", concurrently: true);
```

PostgreSQL has no `CREATE OR REPLACE MATERIALIZED VIEW`, so creating one drops any existing view of the
same name first — and its indexes with it, including the unique index that `concurrently: true`
requires. Create indexes after the view, in the same migration. The drop is not `CASCADE`: a view that
depends on this one makes the statement fail rather than disappear.

SQL Server's nearest equivalent, an indexed view, is maintained on every write rather than refreshed,
so rendering one under this name would mislead; SQL Server, MySQL and SQLite throw.

## Stored procedures

```csharp
migrationBuilder.CreateStoredProcedure(new StoredProcedureDefinition(
    "close_month",
    [
        new SqlParameterDefinition("tenant_id", "integer"),
        new SqlParameterDefinition("total", "numeric(10,2)", SqlParameterDirection.Out),
    ],
    """
    BEGIN
      SELECT sum("Total") INTO total FROM "Orders" WHERE "TenantId" = tenant_id;
    END;
    """));
```

The body is the engine's own routine body, verbatim — procedure languages are not portable, and
inventing one would be worse than not. Only the parts around it are rendered per engine:

| Engine | Create | Drop |
| --- | --- | --- |
| PostgreSQL | `CREATE OR REPLACE PROCEDURE p(IN a int, OUT b int) LANGUAGE plpgsql AS $$ … $$` | `DROP PROCEDURE IF EXISTS p(IN int, OUT int)` from a definition; `DROP PROCEDURE IF EXISTS p` from a name |
| SQL Server | `CREATE OR ALTER PROCEDURE p @a int, @b int OUTPUT AS …` | `DROP PROCEDURE IF EXISTS p` |
| MySQL | `DROP PROCEDURE IF EXISTS p` then `CREATE PROCEDURE p(IN a int, OUT b int) …` | `DROP PROCEDURE IF EXISTS p` |
| SQLite | throws | throws |

On PostgreSQL the dollar-quote tag is chosen so that it does not occur in the body: a body containing
`$$` is quoted with `$body$`, and so on. On MySQL the drop and create are separate statements, never a
`DELIMITER` script — `DELIMITER` belongs to the `mysql` command-line client, not to the server. Pass
`language: "sql"` for a PostgreSQL procedure in plain SQL.

Parameter names must be plain identifiers (letters, digits, underscores), because the body refers to
them bare. SQL Server's `@` is added for you. On PostgreSQL a name
that is a reserved word there (`from`, `order`, `user`, …) is quoted in the definition and in named
calls, as `"from"`; refer to it quoted inside the body too. Other names stay bare, so PostgreSQL folds
them to lower case. On MySQL every parameter name is quoted (`` `select` ``), which changes nothing for
the body's bare references; a reserved one must be quoted in the body as well.

### Calling them

```csharp
int affected = await context.Database.ExecuteProcedureAsync(
    "close_month", new { tenant_id = 7 });

List<MonthTotal> rows = await context.Database.QueryProcedureAsync<MonthTotal>(
    "monthly_totals", new { tenant_id = 7 });
```

Arguments are an object whose properties name them, or an `IReadOnlyDictionary<string, object?>`.
Every value is bound as a parameter through `ExecuteSqlRawAsync` / `SqlQueryRaw`, so the context's
transaction, interceptors, logging and execution strategy apply. That includes retries: under a retrying
strategy (`EnableRetryOnFailure`), a call that fails with a transient error is run again, exactly as EF
runs any raw SQL again — even if the procedure had already committed its writes before the connection
dropped. For a procedure that is not safe to repeat, run the call inside
`CreateExecutionStrategy().ExecuteAsync(...)` with a transaction and a check that it has not already
happened, or use a non-retrying strategy for that context. A value that is already a `DbParameter` is passed through
as is — which is how an output parameter comes back:

```csharp
var total = new SqlParameter("@total", SqlDbType.Decimal) { Direction = ParameterDirection.Output };
await context.Database.ExecuteProcedureAsync("close_month", new { tenant_id = 7, total });
var value = total.Value;   // set by the procedure
```

PostgreSQL matches arguments by name (`CALL p(a => @p0)`), so order does not matter and a procedure's
defaults can be left out. SQL Server matches by name too (`EXEC p @a = @p0`, with `OUTPUT` added for
output parameters). MySQL's `CALL` is positional, so arguments go in the order given, and it takes
input parameters only: MySQL's client libraries cannot bind an `OUT` or `INOUT` parameter to a call
sent as text, so one is refused before anything runs. Return the value as a result set instead, and
read it with `QueryProcedureAsync`.

`QueryProcedureAsync<T>` materialises a `List<T>` rather than returning a query, because a procedure
call cannot be composed over — `EXEC` and `CALL` cannot appear in a `FROM` clause, so a `Where` added
to one would fail at execution. On PostgreSQL the rows are the single row of `OUT` / `INOUT` values that
`CALL` returns; on SQL Server and MySQL, the procedure's first result set. For a scalar `T` the column
must be named `Value`, as everywhere in EF's `SqlQuery`.

## Functions

```csharp
migrationBuilder.CreateFunction(FunctionDefinition.Scalar(
    "add_tax", [new("amount", "numeric")], "numeric",
    "SELECT amount * 1.2", language: "sql", characteristics: "IMMUTABLE"));

migrationBuilder.CreateFunction(FunctionDefinition.Table(
    "orders_for",
    [new("tenant", "integer")],
    [new("Id", "integer"), new("Total", "numeric")],
    """SELECT "Id", "Total" FROM "Orders" WHERE "TenantId" = tenant"""));
```

| Engine | Scalar | Table-valued |
| --- | --- | --- |
| PostgreSQL | `CREATE OR REPLACE FUNCTION … RETURNS type LANGUAGE plpgsql AS $$ … $$` | `… RETURNS TABLE ("Id" integer, …) LANGUAGE sql AS $$ … $$` |
| SQL Server | `CREATE OR ALTER FUNCTION … RETURNS type AS …` | inline: `… RETURNS TABLE AS RETURN ( query )` |
| MySQL | `DROP FUNCTION IF EXISTS` then `CREATE FUNCTION … RETURNS type …` | throws |
| SQLite | throws | throws |

`characteristics` is inserted verbatim where each engine takes routine options: after `LANGUAGE` on
PostgreSQL (`STABLE`, `IMMUTABLE PARALLEL SAFE`), after `RETURNS` on MySQL (`DETERMINISTIC`,
`READS SQL DATA` — one of which MySQL requires when binary logging is on), and as `WITH …` before `AS`
on SQL Server. Function parameters are input-only; an `Out` parameter is refused in favour of a
procedure. SQL Server table functions are always inline, which is the kind its optimiser expands into
the calling query; the column list is used only by PostgreSQL.

### Querying a table function

```csharp
var bigOrders = await context.Database
    .QueryFunction<OrderRow>("orders_for", new { tenant = 7 })
    .Where(o => o.Total > 100)
    .OrderByDescending(o => o.Total)
    .ToListAsync();
```

`QueryFunction<T>` returns an `IQueryable<T>` over `SELECT * FROM orders_for(...)`, so the `Where` and
`OrderBy` run in the database. PostgreSQL takes the arguments by name, SQL Server by position. MySQL has
no table-valued functions and SQLite no user-defined ones; both throw.

---

## Row-level security

### Tenant isolation

The common case in one definition:

```csharp
public static readonly TenantIsolationPolicy OrdersIsolation =
    new("Orders", tenantColumn: "TenantId", sessionKey: "app.tenant_id", columnStoreType: "integer");

migrationBuilder.CreateTenantIsolation(Schema.OrdersIsolation);
migrationBuilder.DropTenantIsolation(Schema.OrdersIsolation);   // in Down()
```

On **PostgreSQL** this renders:

```sql
ALTER TABLE "Orders" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "Orders" FORCE ROW LEVEL SECURITY;
CREATE POLICY "Orders_tenant_isolation" ON "Orders"
    AS RESTRICTIVE FOR ALL TO PUBLIC
    USING ("TenantId" = CAST(NULLIF(current_setting('app.tenant_id', true), '') AS integer))
    WITH CHECK (/* the same */);
CREATE POLICY "Orders_tenant_isolation_access" ON "Orders"
    AS PERMISSIVE FOR ALL TO PUBLIC
    USING (true) WITH CHECK (true);
```

The cast from the session value must also be **lossless**, so for a column type with a length,
precision or scale a second comparison is added. Casting alone would truncate or round: with a
`varchar(8)` tenant column a session carrying `acme-dev2` would match `acme-dev`'s rows, and with
`numeric(10,2)` a session carrying `12.555` would match `12.56`'s. On PostgreSQL the constrained cast
must therefore equal the same value cast without the modifier:

```sql
USING ("TenantId" = CAST(s AS varchar(8)) AND CAST(s AS varchar(8)) = CAST(s AS varchar))
```

Values are compared, not their text, so `12.5` matches a `numeric(10,2)` tenant stored as `12.50`, and
the ISO 8601 timestamp `UseSessionContext` writes for a `DateTime` matches the `timestamp(0)` PostgreSQL
prints with a space; `12:30:00.4` against that column matches nothing. Most types with no modifier
(`integer`, `uuid`, `text`) lose nothing on the cast and get no second comparison. A `date` does: it
drops the time of day, so a `date` tenant also requires the setting, read as a timestamp, to be that
date's midnight — a `DateOnly`, a date string or a `DateTime` at 00:00 matches, 12:34 that day does
not, nor does a tick past midnight. A bare `character`
or `bit` is the exception: without a length it means length 1, so it is compared like `character(1)`;
likewise `name`, which keeps only its first 63 bytes. And PostgreSQL keeps a `timestamp` or `time`
only to the microsecond, rounding finer digits away even without a modifier, so a session value with a
nonzero digit past the sixth (`12:30:00.0000004`) matches nothing; the seven digits .NET writes for a
`DateTime` still match when the last is `0`. A `DateTime` with ticks finer than a microsecond is a
different instant from any stored one, and reaches no rows.

So on PostgreSQL a tenant key must be one of the built-in types above, which the policy casts
losslessly or checks so that it is: an integer, `numeric`/`decimal`, `text`, `varchar`, `character`,
`name`, `bit`, `varbit`, `uuid`, `date`, `timestamp`/`timestamptz` or `time`/`timetz`, with or without
a modifier. Every other built-in type is refused, because each loses something on the way from text
in its own way: an `interval` rounds `0.4 microseconds` away, a `real` makes `16777217` `16777216`, a
`daterange` drops a bound's time of day. So is any array, in `[]` or `ARRAY` form or by its catalog
name (`_float4`), which casts every element that way. 

A temporal key takes its setting in one plain ISO 8601 form only — the one `UseSessionContext` writes —
with every field inside its range, seconds and fraction optional and at most six significant
fractional digits:

| Key | Setting | Written for |
| --- | --- | --- |
| `date` | `2026-11-27`, or that day at an all-zero midnight | `DateOnly`, an unspecified `DateTime` at 00:00 |
| `timestamp` | `2026-11-27T12:30:00.1234560`, a space for the `T` allowed | a `DateTime` of `DateTimeKind.Unspecified` |
| `time` | `12:30:00.1234560` | `TimeOnly` |
| `timestamptz` | a `timestamp` ending in `Z` or an offset such as `+02:00` | a UTC `DateTime`, a `DateTimeOffset` |
| `timetz` | a `time` ending in `Z` or an offset | — |

PostgreSQL's input takes far more — compact times (`T123456`), single-digit fields, zone names, clock
fields past their range that it carries over (`12:34:60` is `12:35:00`, `24:00` the next midnight) —
and a zone-less cast silently drops the time of day or the zone, so anything else matches nothing. A
setting in the right form whose value does not exist — `2026-02-31`, an offset of `+16` — fails closed
too, but loudly: PostgreSQL raises a cast error, which aborts the query. For
a zone-less key that includes a local `DateTime`, which `"O"` writes with its offset, and a UTC one
with its `Z`: use `DateTimeKind.Unspecified`, or `timestamptz` for instants.

A type that is not built in — a domain or a type of your own — is cast by its own
rules, which the policy cannot see into, so a domain over a type that loses data is yours to keep
lossless. A column type containing a SQL comment is refused on every engine, since it could hide what
the cast is to.

On SQL Server a string holding a GUID followed by anything converts to that GUID, and its string
equality ignores trailing spaces. There the converted value must either equal the session value as a
`sql_variant`, which compares a typed value by value (`decimal` 12.5 equals `decimal(10,2)` 12.50, but
never a string with a GUID), or — for a string session value only — read back as its exact text,
compared as bytes. A typed value never takes the text route, since a `float` 1.00000001 and the `real`
1 it rounds to both print as `1`. A character tenant column is passed to the predicate as `nvarchar`, so a UTF-8 `varchar` column
keeps its characters under a Latin-1 database default, and is compared byte for byte with the session
string, lengths included, and only by text: the predicate's parameter takes the database's default collation rather than the
column's, so a case-insensitive default would otherwise give `acme` the rows of `ACME` too, and a
string's `sql_variant` comparison ignores trailing spaces, so `acme ` truncated into an `nvarchar(4)`
column would have read `acme`'s rows. (It also keeps `nvarchar(max)` columns working, which cannot be
converted to `sql_variant` at all.) Tenant keys therefore match exactly, case and trailing spaces
included. Return
the tenant from `UseSessionContext` as its own type (a `Guid`, an `int`, a `decimal`); a string works
only when it is that value's canonical text, so `12.50` for a `decimal(10,2)` tenant and not `12.5`.

The tenant check is **restrictive**. PostgreSQL ORs permissive policies together, so a permissive
tenant check would be bypassed by any other permissive policy on the table — a reporting policy with
`USING (true)` would read across tenants. A restrictive policy is ANDed onto whatever the permissive
ones allow, so nothing else on the table can widen a session past its tenant. Restrictive policies
only ever remove rows, though, and with no permissive policy PostgreSQL grants nothing; the
`_access` policy is that grant. It makes any other permissive policy on the table redundant within a
tenant, but other restrictive policies still apply on top.

When the table's own permissive policies already decide which rows a role may see — published rows
only, say — pass `grantAccess: false`. Only the restrictive tenant policy is created, and those
policies keep deciding within each tenant:

```csharp
new TenantIsolationPolicy("Documents", "TenantId", "app.tenant_id", "integer", grantAccess: false)
```

Applied over an earlier install that had the grant, it also drops that `_access` policy, which would
otherwise go on granting every row of the tenant.

PostgreSQL silently truncates identifiers past 63 bytes, which could merge the tenant policy with its
`_access` counterpart. A name that would be truncated is refused; pass a shorter `policyName`.

`DropTenantIsolation` removes both policies and switches row-level security off again **only if no
other policy remains on the table**, so a rollback never leaves someone else's policy defined but
unenforced.

`FORCE` is the line that is easy to miss: without it the table's **owner** bypasses every policy, and
the owner is usually the role the application connects as. Superusers and `BYPASSRLS` roles bypass
row-level security regardless, so connect as an ordinary role. The key must be a dotted custom setting
name such as `app.tenant_id`.

On **SQL Server** it renders a schema-bound predicate function and a security policy:

```sql
CREATE OR ALTER FUNCTION [dbo].[Orders_tenant_isolation_predicate](@tenant int)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN SELECT 1 AS allowed WHERE @tenant = CAST(SESSION_CONTEXT(N'app.tenant_id') AS int)
    AND (CAST(CAST(SESSION_CONTEXT(N'app.tenant_id') AS int) AS sql_variant) = SESSION_CONTEXT(N'app.tenant_id')
         OR (SQL_VARIANT_PROPERTY(SESSION_CONTEXT(N'app.tenant_id'), 'BaseType') IN ('nvarchar', 'varchar', 'nchar', 'char')
             AND CAST(CAST(CAST(SESSION_CONTEXT(N'app.tenant_id') AS int) AS nvarchar(4000)) AS varbinary(8000))
                 = CAST(CAST(SESSION_CONTEXT(N'app.tenant_id') AS nvarchar(4000)) AS varbinary(8000))
             AND DATALENGTH(CAST(CAST(SESSION_CONTEXT(N'app.tenant_id') AS int) AS nvarchar(4000))) = DATALENGTH(CAST(SESSION_CONTEXT(N'app.tenant_id') AS nvarchar(4000)))));

CREATE SECURITY POLICY [dbo].[Orders_tenant_isolation]
    ADD FILTER PREDICATE [dbo].[Orders_tenant_isolation_predicate]([TenantId]) ON [dbo].[Orders],
    ADD BLOCK PREDICATE [dbo].[Orders_tenant_isolation_predicate]([TenantId]) ON [dbo].[Orders] AFTER INSERT,
    ADD BLOCK PREDICATE [dbo].[Orders_tenant_isolation_predicate]([TenantId]) ON [dbo].[Orders] AFTER UPDATE
WITH (STATE = ON);
```

Both **fail closed**. With no tenant in the session — a request that forgot to set one — the
comparison is `NULL`, so the request sees no rows and can write none, rather than seeing everyone's.
Reads, updates and deletes are filtered to the tenant's rows; inserting a row for another tenant, or
updating a row into one, is refused. MySQL and SQLite have no row-level security and throw.

### PostgreSQL policies

For anything beyond one tenant column:

```csharp
migrationBuilder.EnableRowLevelSecurity("Documents", force: true);
migrationBuilder.CreatePolicy(new RowLevelSecurityPolicy(
    name: "documents_readers",
    table: "Documents",
    usingExpression: """ "OwnerId" = current_setting('app.user_id')::int OR "IsPublic" """,
    command: PolicyCommand.Select,
    kind: PolicyKind.Permissive,
    roles: ["app_user"]));

migrationBuilder.DropPolicy("documents_readers", "Documents");
migrationBuilder.DisableRowLevelSecurity("Documents");   // clears FORCE as well
```

Combinations PostgreSQL would reject — `WITH CHECK` on a `SELECT` policy, `USING` on an `INSERT` one —
are refused when the definition is constructed. `PUBLIC`, `CURRENT_USER`, `CURRENT_ROLE` and
`SESSION_USER` are emitted as keywords; every other role is quoted. PostgreSQL has no
`CREATE OR REPLACE POLICY`, so creating drops a policy of the same name first, inside the migration's
transaction.

### SQL Server security policies

```csharp
migrationBuilder.CreatePolicy(new SqlServerSecurityPolicy(
    name: "DocumentsPolicy",
    table: "Documents",
    functionName: "fn_documents",
    parameters: [new SqlServerPredicateParameter("OwnerId", "int", column: "OwnerId")],
    predicate: "@OwnerId = CAST(SESSION_CONTEXT(N'UserId') AS int) OR IS_ROLEMEMBER('auditors') = 1",
    securitySchema: "security",
    block: SqlServerBlockOperations.AfterInsert | SqlServerBlockOperations.AfterUpdate));
```

The table schema defaults to `dbo` — a security policy must name its function and table in two parts.
A schema-bound function cannot be altered while a policy refers to it, so creating drops the policy,
replaces the function and recreates the policy; dropping removes the policy before the function.

---

## Session context

Row-level security reads the tenant from the session. Set it on every connection open:

```csharp
services.AddScoped<CurrentTenant>();
services.AddDbContext<AppDb>(o => o
    .UseNpgsql(connectionString)
    .UseSessionContext(s => s
        .Set("app.tenant_id", sp => sp.GetRequiredService<CurrentTenant>().Id)));
```

The values are resolved and sent each time EF opens a connection, in one round trip:

| Engine | Statement |
| --- | --- |
| PostgreSQL | `SELECT set_config(@key, @value, false)` |
| SQL Server | `EXEC sp_set_session_context @key = @key, @value = @value` |
| MySQL | `SET @app.tenant_id = @value` |
| SQLite | throws when the context is constructed |

Every open, not once: EF opens and closes the connection around each command by default, and the pool
resets session state between uses (Npgsql's `DISCARD ALL`, SQL Server's `sp_reset_connection`). A value
set once would be gone by the next query — and with a policy that fails closed, that shows up as
queries that quietly return nothing.

`Set(key, sp => …)` resolves from the context's application service provider, which under
`AddDbContext` is the request scope's. Under `AddDbContextPool` the options are shared by every pooled
context and the provider is the root one, where scoped services cannot be resolved — use the
`Set(key, () => …)` overload with an ambient source of the tenant there. A `null` value clears the key.
Setting a key again — in a later `UseSessionContext` call, say — replaces its value in place, and keys
are compared ignoring case, as PostgreSQL and MySQL compare them: `APP.TENANT_ID` overrides
`app.tenant_id`. On SQL Server, whose session context keeps keys differing only in case apart, they
are still one key here.
PostgreSQL values are sent as text formatted with the invariant culture, so the policy's cast reads
`1.5` and ISO dates however the server's culture is set.

For a one-off, without the interceptor:

```csharp
await context.Database.SetSessionContextAsync("app.tenant_id", 7);
```

This opens the context's connection and **leaves it open** until the context is disposed, because a
value set on a connection EF then closed would be reset before the next query ran.

On MySQL, the connectors treat `@name` as a parameter unless user variables are allowed: add
`AllowUserVariables=True` (MySqlConnector, under Pomelo) or `Allow User Variables=True` (Oracle's
provider) to the connection string.

---

## Databases without migrations

Test databases are usually built with `EnsureCreated`, which knows nothing of views, procedures or
policies — a keyless entity mapped `ToView` is assumed to exist already. Apply the same definitions the
migrations use:

```csharp
await context.Database.EnsureCreatedAsync();
await context.Database.CreateDatabaseObjectsAsync(Schema.ActiveCustomers, Schema.OrdersIsolation);

// …and, if the test cleans up
await context.Database.DropDatabaseObjectsAsync(Schema.ActiveCustomers, Schema.OrdersIsolation);
```

Objects are created in the order given and dropped in reverse, so list dependencies first. Every
statement is rendered before the first one runs, so an object the engine cannot have refuses the whole
call without leaving half of it applied. The statements then run as **one transaction** — or, inside
yours, under a savepoint — so a statement the database rejects rolls back the ones before it, and your
transaction is left as it was and still usable. If going back to the savepoint fails as well — SQL
Server under `XACT_ABORT`, say, dooms the whole transaction with the error — the call throws an
`AggregateException` with the statement's error first: the statements before it may then still be in
your transaction, so roll the whole transaction back rather than commit it. A transaction that cannot take a savepoint — an ambient
`TransactionScope`, or SQL Server with `MultipleActiveResultSets`, where EF turns savepoints off — gets
the statements as they are: if one fails, the ones before it are part of your transaction, which may
still be committable, so roll the whole transaction back rather than commit it. That
matters most when replacing a definition, which is a drop followed by a create: re-applying tenant
isolation never shows another session a moment with the tenant policy gone and the grant still in
place. MySQL commits implicitly before every DDL statement, so there the statements run one by one,
and a call made inside a transaction is refused before anything runs — it would otherwise commit the
work already done in that transaction.

"Inside a transaction" means one EF or ADO.NET knows about: `Database.BeginTransaction`,
`Database.UseTransaction`, or an ambient `TransactionScope`. A transaction opened by sending
`START TRANSACTION` as SQL text is invisible to EF itself — its own `SaveChanges` would end it the same
way — and so to these helpers; start transactions through EF when they need protecting. `IDatabaseObject` is the interface all of the definitions
share; `migrationBuilder.CreateDatabaseObject` / `DropDatabaseObject` take any of them, including your
own.

---

## Per-engine support

| | PostgreSQL | SQL Server | MySQL | SQLite |
| --- | --- | --- | --- | --- |
| Views | `CREATE OR REPLACE` | `CREATE OR ALTER` | `CREATE OR REPLACE` | drop + create |
| Materialized views, refresh | ✓ | — | — | — |
| Stored procedures | `CREATE OR REPLACE` | `CREATE OR ALTER` | drop + create | — |
| Calling procedures | `CALL`, by name | `EXEC`, by name | `CALL`, by position | — |
| Scalar functions | ✓ | ✓ | drop + create | — |
| Table-valued functions, `QueryFunction` | ✓, by name | inline, by position | — | — |
| `RowLevelSecurityTable`, `RowLevelSecurityPolicy` | ✓ | — | — | — |
| `SqlServerSecurityPolicy` | — | ✓ | — | — |
| `TenantIsolationPolicy` | RLS + `current_setting` | security policy + `SESSION_CONTEXT` | — | — |
| Session context | `set_config` | `sp_set_session_context` | user variable | — |

Every "—" throws `SqlFeatureNotSupportedException`, with `Feature` and `Dialect` set and, where there is
one, the nearest alternative in the message.

---

## How it works

**Dialect.** `MigrationBuilder.ActiveProvider` and `DatabaseFacade.ProviderName` both report the
provider assembly's name, and that is what is matched — exactly, against the five providers listed
under [Install](#install). A dialect is never configured by hand: the provider already decides what SQL
the rest of the application speaks, and a second setting that could disagree with it would only be a
way to emit SQL for the wrong engine.

**Names and values.** Every name is quoted, with the engine's quote character doubled inside it
(`"we""ird"`, `[a]]b]`, `` `a``b` ``), even names that would be legal bare — a reserved-word list per
engine and version is never complete, and names EF mapped (`Order`, `User`) are exactly the ones that
collide. The exceptions are PostgreSQL languages and routine parameter names, which must be plain
identifiers because the body refers to them unquoted. PostgreSQL quotes a parameter name only when it
is reserved, since quoting would stop it folding to lower case; MySQL quotes every one. Values never reach SQL text at runtime: they are
`{0}` placeholders that EF binds as parameters. The only literal this package renders is a
session-context key inside a generated policy, escaped per engine.

**Statements, not scripts.** Each definition returns a list of statements, and each becomes its own
`migrationBuilder.Sql(...)` operation or its own command. SQL Server requires `CREATE VIEW` and
`CREATE PROCEDURE` to start their batch, and MySQL has no server-side batch separator, so a single
script would be wrong on one engine or the other.

**Session context** is applied by a `DbConnectionInterceptor` added once by `UseSessionContext`. It is
stateless and reads the configured values back from the opening context's options, and the options
extension that carries them registers nothing in EF's internal service provider — so per-request
delegates do not split EF's service-provider cache, which would otherwise build a new internal provider
per request until EF refused to build more.

---

## Limitations

- **Routine and view bodies are not translated.** The SQL inside a view, procedure, function or policy
  expression is the author's, verbatim. A migration replayed against two engines needs a body valid on
  both — or a definition per engine.
- **PostgreSQL routines are dropped by signature from a definition, by name from a name.**
  `DropDatabaseObjectsAsync(definition)` and `migrationBuilder.DropDatabaseObject(definition)` render the
  definition's argument list (`DROP FUNCTION IF EXISTS lookup(integer)`), so only that overload goes.
  `DropFunction("lookup")` and `DropStoredProcedure("p")` take a name alone, which is the only form all
  three engines share: on PostgreSQL it drops the routine only while it is not overloaded — and if just
  `lookup(text)` is left, it drops that one. For an overloaded routine, roll back with the definition:
  `migrationBuilder.DropDatabaseObject(Schema.LookupByNumber)`. `CREATE OR REPLACE` with changed
  parameter types creates an overload beside the old routine rather than replacing it — drop the old
  signature first.
- **PostgreSQL views cannot change their columns in place.** `CREATE OR REPLACE VIEW` may add columns at
  the end but not remove, rename or retype them; PostgreSQL refuses, and the view must be dropped first.
- **Materialized views lose their indexes when recreated.** See [Materialized views](#materialized-views).
- **Session context needs EF to open the connection.** A connection the application opened itself and
  handed to the provider is never opened by EF, so the interceptor never runs; let EF own it, or call
  `SetSessionContextAsync`.
- **Session context keys are not read-only.** SQL Server's `sp_set_session_context @read_only = 1` is
  not used, so code running on the same connection can change the tenant. Row-level security protects
  against queries that forget the tenant, not against code that sets another one deliberately.
- **MySQL backslashes.** String literals are escaped for MySQL's default mode, in which a backslash is
  an escape character. Under `NO_BACKSLASH_ESCAPES` a literal containing one comes out doubled. Runtime
  values are parameters and unaffected.
- **Integration coverage.** Every dialect's rendering has exact-SQL unit tests. Against real engines,
  PostgreSQL covers row-level security, tenant isolation, routines and views; SQL Server covers tenant
  isolation (typed and string session values, lossy conversions, a binary-collated tenant column under a
  case-insensitive database); MySQL covers runtime DDL and procedures with reserved parameter names.
