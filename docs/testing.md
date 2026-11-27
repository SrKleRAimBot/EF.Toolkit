# EF.Toolkit.Testing

The things every EF Core test suite ends up writing for itself — a database per test, a way to empty
it, data to put in it, and a way to see what SQL actually ran.

- **Test databases.** In-memory SQLite that behaves like a real database, or any real engine through
  a provider delegate. One call creates the schema, seeds it, and hands out fresh contexts.
- **Each test in a rolled-back transaction.** An isolated scope whose contexts share one connection
  and one transaction, rolled back on dispose.
- **Data reset, from the model.** Every mapped table emptied in foreign-key-safe order, identity
  counters restarted, in one call — self-references and cycles included.
- **Seeding that does not lie to you.** Seeded entities are saved and then *detached*, so the
  assertion that follows reads the database rather than the instance the test just created.
- **A model-driven builder.** `context.Build<Order>()` fills every required column with a unique,
  valid value and builds the required customer too; the test states only what it is about.
- **SQL capture.** Record the commands a block of code ran and assert on them — the way to catch an
  N+1 query before production does.
- **Model assertions.** A forgotten migration, a renamed table, a dropped index — each a one-line test.

**It is test-framework agnostic.** Nothing here references xUnit, NUnit or MSTest. A failed assertion
throws `EFToolkitAssertionException`, which every runner reports as a failure with its message.

---

## Contents

[Install](#install) · [Setup](#setup) · [Test databases](#test-databases) —
[SQLite](#in-memory-sqlite), [real engines](#real-engines), [seeders](#seeders) ·
[Isolated scopes](#isolated-scopes) · [Resetting data](#resetting-data) · [Seeding](#seeding) ·
[Building test data](#building-test-data) · [SQL capture](#sql-capture) ·
[Model assertions](#model-assertions) · [How it works](#how-it-works) · [Limitations](#limitations)

---

## Install

```bash
dotnet add package EF.Toolkit.Testing
```

Into the test project, not the application. It brings `Microsoft.EntityFrameworkCore.Sqlite` with it
— the bundled build, native SQLite included — so `SqliteTestDatabase` works with nothing else
installed. For a real engine, add its provider as the application already does.

## Setup

Everything public is in `EFToolkit.Testing`, except the extension methods on `DbContext` and
`DatabaseFacade`, which sit in `Microsoft.EntityFrameworkCore` beside EF's own. One using:

```csharp
using EFToolkit.Testing;
```

The context needs a public constructor taking `DbContextOptions<TContext>` (or `DbContextOptions`) —
the one `AddDbContext` already requires. A context that takes more gets a factory:

```csharp
o => o.UseContextFactory(options => new AppDb(options, clock))
```

---

## Test databases

### In-memory SQLite

```csharp
await using var db = await SqliteTestDatabase<AppDb>.CreateAsync(o => o
    .ConfigureOptions(b => b.UseBulkOperations())       // anything after the provider
    .Seed(ctx => ctx.Countries.Add(new Country("NZ"))));

await using var context = db.CreateContext();
```

The schema is created with `EnsureCreated` (or `UseMigrations()`), the seeders run once, and every
`CreateContext()` is a new context on the same database. Nothing touches the disk, so creation is cheap
and the usual shape is one database per test.

An in-memory SQLite database lives only as long as a connection to it is open, and each new
connection to `:memory:` is a different, empty database. So the connection is opened once, held for
the database's lifetime, and shared by every context — that is what makes the data written through
one context visible to the next. Disposing the database closes it.

Unlike EF's in-memory provider this is a real relational engine: foreign keys, unique indexes,
transactions, `ExecuteUpdate` and SQL translation all behave as they do on disk.

### Real engines

`TestDatabase<TContext>` takes the provider as a delegate, so it works on any of them:

```csharp
await using var db = await TestDatabase<AppDb>.CreateAsync(
    o => o.UseNpgsql(container.GetConnectionString()),
    o => o.UseMigrations().DeleteExisting());
```

| Setting | Default | |
| --- | --- | --- |
| `CreateWith(DatabaseCreationStrategy)` | `EnsureCreated` | `Migrate` runs the application's migrations; `None` assumes the schema exists. |
| `UseMigrations()` | | Shorthand for `CreateWith(Migrate)`. |
| `DeleteExisting()` | off | Drops the database first, so a schema left by an earlier run is replaced. |
| `ConfigureOptions(...)` | | Applied after the provider, to every context. May be called more than once. |
| `UseContextFactory(...)` | reflection | Builds contexts whose constructor takes more than the options. |
| `ResetWith(...)` | | Configures the reset `db.ResetAsync()` performs. |
| `WithTestDataSeed(n)` | `0` | Seeds `db.Data`, the database's [test-data builder](#building-test-data). |

Starting the engine is the test's business. Testcontainers is the usual answer, and this package
deliberately does not depend on it:

```csharp
public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public TestDatabase<AppDb> Db { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Db = await TestDatabase<AppDb>.CreateAsync(o => o.UseNpgsql(_container.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
```

Disposing a `TestDatabase` leaves the database in place; dropping it is the container's job.

### Seeders

```csharp
o.Seed(ctx => ctx.Countries.AddRange(countries))       // Action<TContext>
 .Seed(async (ctx, ct) => await LoadFixtureAsync(ctx, ct))
 .Seed(new ReferenceDataSeeder())                      // ISeeder<TContext>
```

Seeders run in registration order, each on a fresh context, and whatever a seeder leaves unsaved is
saved after it. They run once at creation and again after every `db.ResetAsync()`, which empties the
tables and puts the seed data back — the reset for a fixture shared by many tests.

---

## Isolated scopes

The classic pattern: every test runs inside a transaction that is rolled back at the end, so nothing
it writes survives and the next test starts from the seeded state.

```csharp
await using var scope = await db.BeginIsolatedScopeAsync();

var arrange = scope.CreateContext();
await arrange.SeedAsync(order);

var act = scope.CreateContext();
await new OrderService(act).ShipAsync(order.Id);

var assert = scope.CreateContext();
(await assert.Orders.SingleAsync()).Status.ShouldBe(OrderStatus.Shipped);
// disposed: rolled back
```

Every context from the scope shares one connection and one transaction, so they see each other's
uncommitted writes. `SaveChanges` joins the transaction. An isolation level can be passed:
`BeginIsolatedScopeAsync(IsolationLevel.Serializable)`.

It works the same on SQLite and on every real engine, and a [reset](#resetting-data) inside a scope is
rolled back with it.

Code under test that calls `BeginTransaction` itself cannot run inside a scope: the context already
has a transaction and EF refuses a second. Use a reset between tests for that code instead.

---

## Resetting data

```csharp
await context.Database.ResetDataAsync();
await context.Database.ResetDataAsync(o =>
{
    o.ExcludeEntity<Country>()       // reference data stays
     .ExcludeTable("Settings", "app");
    o.RestartIdentity = false;       // default true
});
```

`ExcludeTable` matches by name, and by schema when you give one. A model table with no schema of its
own lives in the connection's default schema, which the model cannot know, so a schema-qualified
exclusion still matches it — erring toward keeping rows.

Every table the model maps is emptied: tables only, not views, `ToSqlQuery` entities or keyless types
without a table, and only this context's — a migrations history table, or the tables of another
context sharing the database, are left alone. The order comes from the model's foreign keys,
dependents first. Identity counters restart, so the next row inserted gets id 1 again. The context's
change tracker is cleared afterwards.

What each engine is sent:

| Engine | Statements |
| --- | --- |
| PostgreSQL | One `TRUNCATE TABLE a, b, ... RESTART IDENTITY`, without `CASCADE`. `TRUNCATE` checks the foreign keys between the listed tables as a set, so no order is needed. |
| SQL Server | `DELETE` in dependency order, then `DBCC CHECKIDENT ... RESEED` for each table with an identity column. Tables on a foreign-key cycle — self-references included — have `NOCHECK CONSTRAINT ALL` for the delete and `WITH CHECK CHECK CONSTRAINT ALL` after, so the constraints come back trusted. Both run inside the reset's transaction, so a re-check that fails rolls back the deletes and the suspension together. |
| MySQL | `DELETE` in dependency order with foreign-key checks left on, so the engine itself refuses a delete that would orphan a row — even in a table the login cannot see — then `ALTER TABLE ... AUTO_INCREMENT = 1` for each table after the commit. Only a plan with a foreign-key cycle switches the checks off (`SET FOREIGN_KEY_CHECKS = 0`, then back on). Then every foreign key in every database the login can see is checked before the commit, because MySQL never re-checks a row written while checks were off and a `DELETE` trigger could write anywhere: no table may have any violation afterwards, and a table outside the reset that already has violations makes the reset refuse before it starts, since a new orphan could not be told from an old one. Every MySQL reset needs a login with a table privilege on the whole database — granted on the database, or globally without `partial_revokes` — or it is refused: a login granted only some tables cannot see the others or their foreign keys, so an `ON DELETE CASCADE` there would empty rows it never checked. Tables that ignore transactions (MyISAM, MEMORY) are refused too, since a rollback could not restore them. |
| SQLite | `PRAGMA foreign_keys = OFF`, `DELETE` each, clear `sqlite_sequence` for those tables, then back on. |

The deletes run as **one unit**: a transaction, or a savepoint inside yours when one is open. A
reset that fails part way — a trigger, a lock timeout, a cancellation — rolls back every table it had
already emptied, so it never leaves some tables empty and others not. MySQL's counter restart is the
one step outside the unit, after the commit, since `ALTER TABLE` commits on its own: if it fails, the
rows are already gone and the error says so. Before the unit closes, every table is checked empty wherever a
trigger could have kept a row or put one back — an `INSTEAD OF DELETE` soft delete on SQL Server, a
`RAISE(IGNORE)` on SQLite, an audit trigger writing into a table MySQL already emptied, an `AFTER
TRUNCATE` trigger on PostgreSQL — and a table left with rows fails the reset, names the table and rolls
it back. On PostgreSQL the check runs when a `TRUNCATE` trigger — the only kind
that fires — sits on any reset table or on a partition or child `TRUNCATE` reaches through one, and then
covers every reset table, since such a trigger can write anywhere; a reset table under row-level
security that applies to the login is refused then, since a count it filters could not show what is
left. The unit's savepoint gets a fresh name for every reset,
so it can never take the place of one of yours.

Whatever is suspended is restored even when a statement fails, and every restore is attempted even if
one of them fails — those settings belong to the connection or the table, and a pooled connection
handed back with foreign-key checks off would disable them for whichever test opened it next. A
setting that was already off is left off — with one exception: on SQL Server a table on a foreign-key
cycle has `WITH CHECK CHECK CONSTRAINT ALL` run after its delete, which re-enables every constraint on
it, including a check or foreign key that was disabled on purpose (see the limitations below). If a restore fails too, the reset's own error and the
restore's are raised together in an `AggregateException`, the original first.

The SQL Server reseed reads the table's own seed and increment and whether it has ever generated a
value — `RESEED, n` means "next is `n`" on a table that never has, and "next is `n` plus the
increment" on one that has — so the next row gets the seed value either way.

Inside a transaction the reset joins it, so it is rolled back with an isolated scope. Two engines
need different statements there: MySQL does not restart identity, because `ALTER TABLE` commits the
open transaction; SQLite defers its foreign keys to commit rather
than switching them off, which it cannot do inside a transaction.

The engine is read off `Database.ProviderName`: `Npgsql.EntityFrameworkCore.PostgreSQL`,
`Microsoft.EntityFrameworkCore.SqlServer`, `MySql.EntityFrameworkCore`,
`Pomelo.EntityFrameworkCore.MySql` and `Microsoft.EntityFrameworkCore.Sqlite`. Any other provider is a
`NotSupportedException`.

---

## Seeding

```csharp
var (alice, order) = (new Customer("Alice"), new Order { Total = 10 });
await context.SeedAsync(alice, order);
alice.Id;                                      // populated

var customers = await context.SeedAsync(list); // IReadOnlyList<Customer>
var one = await context.SeedOneAsync(customer);
context.Seed(alice, order);                    // synchronous
```

Each call adds the entities with their graphs, saves, and then **clears the change tracker**. That
last step is the point. Left tracked, the seeded instances sit in the context's identity map, and a
later query on the same context hands those very instances back instead of reading the rows — so an
assertion meant to check what the code under test wrote checks what the test put in memory, and
passes either way.

The whole tracker is cleared, not just the seeded entities, and anything else pending on the context
is saved with them. Seed before acting, not halfway through. A failed seed clears it too, so a
half-added graph is not retried by the test's next `SaveChanges`.

---

## Building test data

```csharp
var order = context.Build<Order>(o => o.Total = 0m);   // not saved
order.Customer;                                         // built too: the reference is required

var line = await context.CreateAsync<OrderLine>();      // built and inserted, principals and all
var many = await context.CreateManyAsync<Customer>(50, (c, i) => c.Name = $"Customer {i}");
```

`Build` reads the model and fills every **required** property with a value that is valid for its
column and **unique** — one counter runs across every value produced, so two entities never share a
generated string or number and a unique index is never tripped by accident. A column with only so many
values repeats past them: a `decimal(2,2)` holds 100, a `bool` two.

| Type | Value |
| --- | --- |
| `string` | `PropertyName-n`, cut to fit `MaxLength` with the counter kept, in base 36 when space is tight |
| integers, `float`, `double` | the counter |
| `decimal` | the counter, kept inside the column's precision — `decimal(5,2)` never overflows; a PostgreSQL negative scale such as `numeric(2,-3)` steps by its rounding unit |
| `Guid` | from the seeded generator |
| `DateTime`, `DateTimeOffset` | from 2000-01-01, UTC (unspecified kind for an Npgsql `timestamp without time zone` column) |
| `DateOnly`, `TimeOnly`, `TimeSpan` | from the counter, inside every engine's range |
| `bool`, enums | drawn from the seeded generator; enums only ever get a defined member |
| `byte[]` | up to eight bytes of the counter, cut to `MaxLength` |
| primitive collections | one element |

What is left alone:

- **Nullable properties** stay `null` — pass `fillOptional: true` to fill them, and optional owned
  references, too.
- **Store-generated properties** — identity keys, `HasDefaultValueSql`, computed columns, row
  versions: anything whose `ValueGenerated` is not `Never` is the database's to fill.
- **Foreign-key properties.** A required reference is satisfied by building the principal instead,
  recursively, up to a depth of 3. Nothing is reused: each entity gets principals of its own. To share
  one, set the navigation — or the foreign key, in which case the generated principal is dropped
  again so the key you chose is the one that is saved.
- **Types it does not know** — including a value-converted property whose CLR type is not in the table
  above, such as a strongly typed id. These keep whatever the constructor gave them.

Required complex properties and required owned references are filled the same way, recursively.

**Deterministic.** The same seed and the same sequence of calls build the same values. A context
created by a test database uses that database's builder (`db.Data`, seeded by `WithTestDataSeed`);
any other context uses `TestData.Shared`. For full control, use an instance directly:

```csharp
var data = new TestData(seed: 42, maxDepth: 2);
var customer = data.Build<Customer>(context);
```

---

## SQL capture

```csharp
using var sql = db.CaptureSql();

foreach (var order in await context.Orders.ToListAsync())
    await context.Entry(order).Reference(o => o.Customer).LoadAsync();   // one query per order

sql.ShouldHaveExecutedAtMost(1);                // fails, listing every command that ran
```

| | |
| --- | --- |
| `Commands` | Each command's text, parameters (as they were when it ran), kind — `Reader`, `NonQuery` or `Scalar` — duration and whether it succeeded. |
| `ShouldHaveExecuted(n)` | Exactly `n` commands. |
| `ShouldHaveExecutedAtMost(n)` | No more than `n` — the N+1 guard. |
| `ShouldContain(text)` / `ShouldNotContain(text)` | Case-insensitive by default; pass a `StringComparison` to change it. |
| `Clear()` | Discards what was recorded and keeps recording — seed, clear, then assert on the act alone. |

Each assertion returns the capture, so they chain, and a failure lists the commands that ran.

Recording is scoped twice. In time: only commands between `CaptureSql()` and `Dispose()` are kept.
And in flow: a capture sees commands on its own asynchronous flow and on the tasks that flow starts,
never those of a test running alongside it — even one on the same database, through the same
interceptor. Tests running in parallel do not bleed into each other's captures.

Three sources:

- **`db.CaptureSql()`** — every context the test database creates, scopes included.
- **`context.CaptureSql()`** — that context only.
- **`SqlCapture.Start()`** — any context with the capture interceptor.

Interceptors are fixed when the options are built, long before a test knows it wants to capture
anything, so the test databases always register `SqlCaptureInterceptor`; it costs one read of an
`AsyncLocal` per command until a capture is started. For a context built elsewhere, add it yourself:

```csharp
options.UseSqlite(connection).UseSqlCapture();
```

---

## Model assertions

```csharp
context.ShouldHaveNoPendingModelChanges();
context.ShouldMapTable<Order>("orders", schema: "sales");
context.ShouldHaveIndex<Order>(o => new { o.CustomerId, o.PlacedAt });
context.ShouldHaveIndex<User>(u => u.Email, unique: true);
```

**`ShouldHaveNoPendingModelChanges`** is the test that catches a model change merged without its
migration: in CI, the forgotten `dotnet ef migrations add` fails the build instead of the deployment.
It asks EF's `HasPendingModelChanges()` and, when there are changes, lists what a new migration would
contain:

```
'AppDb' has model changes that are not in a migration.
A new migration would contain:
  - AddColumn Orders.Notes
  - CreateIndex Orders.IX_Orders_PlacedAt
Add a migration with: dotnet ef migrations add <Name>
```

**`ShouldMapTable`** checks the table and, when given, the schema; a type mapped to a view says so.
**`ShouldHaveIndex`** checks for an index over exactly those properties *in that order* — order
matters to the database, since an index on `(A, B)` serves a filter on `A` alone and one on `(B, A)`
does not — and, when given, its uniqueness. A miss lists the indexes there are. Primary keys are not
indexes here.

They read the model alone; no database is touched.

---

## How it works

**The reset is planned from the model, not the catalog.** `IRelationalModel.Tables` is exactly the
set of things that hold rows — views, SQL queries and functions are separate kinds of object in it —
and its foreign-key constraints give the order. The graph is split into strongly connected components
(Tarjan's algorithm, iteratively, so a long chain cannot overflow the stack): components come out
principals-first and are reversed, and any component of more than one table, or a table that
references itself, is a cycle. Planning and SQL generation are separate from execution, so what each
engine is sent is unit-tested without the engine.

The reset runs straight on the ADO.NET connection rather than through `ExecuteSqlRaw`, so it does not
appear in an SQL capture that is asserting on the test's own commands.

**Isolated scopes** keep one root context alive for the scope's lifetime; it opens the connection and
begins the transaction. Every other context is pointed at that connection with `SetDbConnection`
before it opens one of its own, and enlisted with `UseTransaction`.

**SQL capture** is a `DbCommandInterceptor` that hands each finished or failed command to the
captures held in an `AsyncLocal` — which is what follows the test's asynchronous flow and nobody
else's. Each capture filters by source: a context, a test database's interceptor, or anything.

**The builder** walks `IEntityType` metadata: properties, complex properties, foreign keys and owned
navigations. Entities are instantiated through a parameterless constructor where there is one (so
collection initialisers run), and members set through their setter or, for read-only properties, the
backing field EF maps.

---

## Limitations

- **SQLite is not your production engine.** It is a real relational database, but its type system,
  collation, date handling and SQL dialect differ. A query that translates on SQLite may not on
  PostgreSQL and vice versa. Run the suite that matters against the real engine with `TestDatabase`.
- **An in-memory SQLite database is single-threaded.** Its one connection is shared by every context,
  so contexts from one database must not run commands concurrently. Give parallel tests a database
  each. For the same reason, while an isolated scope is open on one, a context created from the
  database directly rather than from the scope cannot run a command: the shared connection has a
  transaction that context is not enlisted in.
- **The reset only knows the model.** A table the model does not map is not emptied. If one has a
  foreign key into a mapped table, the reset is refused before anything is deleted: on SQLite, MySQL
  and SQL Server it reads the database's own foreign keys first, and on PostgreSQL the single
  `TRUNCATE` fails as a whole. Map the table, or drop the foreign key.
- **PostgreSQL inheritance is checked too.** `TRUNCATE` empties every table that inherits from the ones
  it names. Partitions of a mapped partitioned table are that table's rows and are emptied with it,
  but an unmapped `INHERITS` child — an archive, say — or a partition the reset was told to keep,
  whether the model maps it or it was only named in `ExcludeTable`, makes the reset refuse instead.
- **Excluding a table that references a reset one is refused** before anything runs, on every engine:
  the reset would either delete the kept rows with their principals or orphan them. Exclude the
  principal too, or stop excluding the dependent.
- **A SQL Server table under an enabled row-level-security filter is refused.** The filter applies to
  `DELETE` as to `SELECT`, so the reset would remove only the rows the session can see and report
  success. Turn the policy off for the reset (`ALTER SECURITY POLICY ... WITH (STATE = OFF)`) or
  exclude the table. The check needs to see every policy, so the reset also refuses a login without
  `VIEW DEFINITION` on the database (db_owner has it), or SQL Server 2022's narrower
  `VIEW SECURITY DEFINITION`: to such a login a policy can be invisible.
- **A transaction counts when EF or ADO.NET knows about it** — `Database.BeginTransaction`,
  `UseTransaction`, an isolated scope, or an ambient `TransactionScope`. One opened by sending
  `START TRANSACTION` as SQL text is invisible to EF itself, whose own `SaveChanges` would end it the
  same way; on MySQL a reset would commit it. Start transactions through EF.
- **MySQL cannot restart identity inside a transaction.** `ALTER TABLE` commits implicitly, so inside
  an isolated scope the counters carry on.
- **A mapped table that is a view in the database is refused** on MySQL, SQL Server and SQLite. Deleting
  from an updatable view deletes from its base table, and on SQLite an `INSTEAD OF` trigger runs in its
  place; either way the reset would touch a table it was never asked to. (PostgreSQL's `TRUNCATE`
  refuses a view itself.)
- **SQLite names are matched with the built-in `NOCASE` collation**, which folds ASCII letters only,
  exactly as SQLite matches identifiers. An application that overrides `NOCASE` (Microsoft.Data.Sqlite
  allows it, for Unicode-aware comparisons) changes that: two tables whose names differ only in the case
  of a non-ASCII letter would then be taken for one. Keep the built-in `NOCASE` on connections a reset
  runs on.
- **A trigger's writes are only as transactional as the tables they land in.** A `DELETE` trigger on a
  reset table that writes to a MyISAM log keeps that write when the reset rolls back.
- **A MySQL temporary table named like a mapped one is refused** — or like any table the foreign-key
  check reads. MySQL resolves the name to the session's temporary table first, even qualified with the
  database, so the reset would empty it, or check it, instead. Drop it, or reset on another connection.
- **MySQL foreign keys in a database the login has no access to are out of reach.** The reset sees
  every table in its own database, which its privilege guarantees, and in any other database it has a
  privilege on. A foreign key from a database it cannot see at all — whose `ON DELETE CASCADE` would
  act on the reset, or whose rows a trigger could orphan while checks are off — is hidden from it, as
  from every client short of a server-wide privilege. Run the reset as a login that can see every
  database that references the test database.
- **SQL Server re-enables every constraint on a cyclic table**, including one that was disabled on
  purpose. Temporal tables keep their history.
- **`ResetDataAsync` clears the change tracker, and `SeedAsync` saves everything pending.** Both act on
  the whole context, by design.
- **The builder does not satisfy every model.** A required reference without a navigation, a required
  shadow property, or a required property of a type it does not generate is left for the test to set.
  A required self-reference, or a cycle of required references, has no finite graph that satisfies
  it; past the depth limit the reference is left unset. Value converters are not consulted: a value is
  generated for the CLR type, so a converter that rejects some values of that type can reject it.
- **Capture follows the flow that started it.** A capture created inside a helper method's `await`
  chain is not visible to the caller after the helper returns; create it in the test itself.
- **`ShouldHaveNoPendingModelChanges` needs migrations** in the context's migrations assembly. With
  none, it fails and says so.

## License

MIT
