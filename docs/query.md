# EF.Toolkit.Query

Standard query patterns for EF Core, so that pagination, sorting and filtering are written once and
work the same way everywhere.

- **Two pagination models, both correct.** Offset paging for numbered pages, keyset paging for
  everything deep or infinitely scrolled — with the lexicographic comparison that composite keyset
  paging actually needs, not the one that quietly drops tied rows.
- **A total ordering, guaranteed.** A sort specification appends a tiebreaker to every ordering, so
  no two rows compare equal and no row can land on two pages or on none.
- **Fluent defaults.** Page size, numbering base and counting strategy are configured once on the
  context and overridable per query.
- **Composable filters.** Conditional `Where`, `IN` lists, half-open ranges and allowlisted free-text
  search, all splicing into expression trees EF can translate.
- **Ambient tracking scopes.** A `using` block that makes every query inside it no-tracking, nesting
  correctly, honoured through EF's compiled-query cache.
- **A development-time advisor.** Reports missing indexes, non-deterministic ordering, deep offsets
  and cartesian `Include`s — from the model alone, with no database round trip. Off by default.

**It is not a query abstraction.** There is no repository, no specification pattern, no wrapped
`IQueryable` and no custom provider. Every entry point is an extension method that takes an
`IQueryable<T>` and hands one back, so the calling code keeps writing ordinary LINQ against EF.

---

## Contents

[Install](#install) · [Setup](#setup) · [Offset pagination](#offset-pagination) ·
[Keyset pagination](#keyset-pagination) — [cursors](#cursors), [what it refuses](#what-keyset-paging-refuses) ·
[Sorting](#sorting) · [Filtering and search](#filtering-and-search) · [Streaming](#streaming) ·
[Tracking scopes](#tracking-scopes) · [Options](#options) · [Diagnostics](#diagnostics) ·
[How it works](#how-it-works) · [Limitations](#limitations) · [Sample](#sample)

---

## Install

```bash
dotnet add package EF.Toolkit.Query
```

One package. Unlike `EF.Toolkit.Bulk` and `EF.Toolkit.Audit` there is no provider variant to choose:
nothing here replaces a provider-specific service.

## Setup

One call, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseNpgsql(connectionString)
    .UseQueryHelpers());
```

Configuration is optional; everything has a default. See [Options](#options).

---

## Offset pagination

The right shape for a numbered-page interface, where the caller jumps to page 47 and sees how many
pages there are.

```csharp
var page = await context.Orders
    .Where(o => o.Total > 100)
    .OrderBy(o => o.PlacedAt).ThenBy(o => o.Id)
    .ToPagedResultAsync(context, PageRequest.Of(pageNumber, pageSize));

page.Items;        // IReadOnlyList<Order>
page.TotalCount;   // long?  — null unless the strategy counted
page.TotalPages;   // int?
page.HasNext;      // bool?  — null under PageCountStrategy.None
page.HasPrevious;
```

`PageRequest.Of(pageNumber, pageSize)` takes a nullable page size: leaving it out uses the configured
default, and a size above `MaxPageSize` is **clamped, not refused**. That value usually arrives from a
query string, so a ceiling that throws just turns `?pageSize=1000000` into a way to generate 500s.

Three ways to establish what lies beyond the page, set by `CountStrategy`:

| Strategy | Round trips | `TotalCount` | `HasNext` |
| --- | --- | --- | --- |
| `TotalCount` (default) | 2 | exact | exact |
| `HasNextProbe` | 1 | `null` | exact |
| `None` | 1 | `null` | `null` |

`HasNextProbe` fetches one row more than the page and discards it. That row is the whole answer to
"is there more", and it costs a row rather than the full scan a `COUNT` performs.

There is also an overload without the `DbContext`, for code that does not have one to hand. It uses
`QueryOptions.Default` and runs no advisory checks.

## Keyset pagination

The right shape for infinite scroll, for APIs that hand out a "next" link, and for anything deep
enough that the offset itself becomes the cost. A page costs the same wherever it sits, and a row
inserted or removed elsewhere cannot shift the boundary — so no row is shown twice or skipped.

```csharp
static readonly KeysetDefinition<Order> ByNewest = KeysetDefinition.For<Order>(k => k
    .Descending(o => o.PlacedAt)
    .Ascending(o => o.Id));          // ends in a unique column

var page = await context.Orders
    .Where(o => o.Total > 100)
    .ToKeysetPageAsync(context, ByNewest, pageSize: 50, cursor);

return new { items = page.Items, next = page.Next?.Token, previous = page.Previous?.Token };
```

Do not order the query yourself — the definition supplies the whole ordering. There is no page number
and no total: pages are reached by walking, not by jumping.

A definition is immutable and compiles its key accessors once, so declare it as a `static readonly`
field rather than rebuilding it per request.

### Cursors

`KeysetCursor.Token` is an opaque string safe to put in a URL. Read one back with
`KeysetCursor.TryParse`, which reports a malformed token instead of throwing — a bad cursor is an
ordinary bad request, and the usual response is to serve the first page.

Every cursor carries a fingerprint of the ordering it was issued for. Replaying one against a
different sort is refused rather than answered, because the boundary values would be compared against
the wrong columns and the page returned would be arbitrary.

> **Opaque, not secret.** The token is Base64Url over plain text, so anyone can decode it and read the
> key values of the row it points at, and anyone can mint one. It is tamper-*evident*, not
> tamper-*proof*. Do not put anything in a keyset ordering that the caller is not already allowed to
> see, and do not treat holding a cursor as permission to read the rows past it.

### What keyset paging refuses

Each of these is a `QueryNotSupportedException` rather than a warning, because each one produces a
page that is wrong without saying so.

| Refused | Why |
| --- | --- |
| A nullable column | Engines disagree about where `NULL` sorts — SQL Server puts it first ascending, PostgreSQL last — and a comparison against `NULL` is neither true nor false, so every row with one is skipped by every page. |
| A value-converted column | `ORDER BY` and the page comparison both run against the *stored* value. An enum written as text sorts alphabetically, not by its numbering. Opt out with `AllowConvertedKey()` where the conversion preserves order. An enum stored as its own underlying number is allowed automatically. |
| An ordering that cannot break every tie | Checked against the keys and unique indexes of the model. Without one, the boundary falls in an arbitrary place among tied rows. Opt out with `AllowNonUniqueKey()` where uniqueness is guaranteed outside the model. |
| A computed expression | The value has to be readable back off a materialised row to build the next cursor, so each component must be a plain property. |
| A type a cursor cannot carry | Anything without a round-trippable text form. |

## Sorting

A sort specification is an allowlist. The caller supplies a field *name*; the name is looked up here
rather than resolved against the model, so an API-supplied sort cannot reach a column that was not
offered.

```csharp
static readonly SortSpecification<Order> OrderSort = SortSpecification.For<Order>(s => s
    .Allow("placed", o => o.PlacedAt)
    .Allow("total", o => o.Total)
    .DefaultOrder("placed", SortDirection.Descending)
    .Tiebreaker(o => o.Id));

var ordered = context.Orders.OrderBy(OrderSort, sortQueryString);
```

`sortQueryString` is a comma-separated list — `"total:desc,placed"`. A term may name its direction
with `:asc` / `:desc` or a leading `-`; with neither it ascends. Blank applies the default ordering.

An unknown field **throws and lists the allowed names**. Skipping it would return rows in an order the
caller did not ask for, with nothing in the response to notice it by — and the name usually comes
straight off a query string, so a client-side typo would become a silently wrong page.

The tiebreaker is appended to every ordering, and not appended twice when the caller sorts by that
column explicitly. It is what makes any paginated result reproducible.

## Filtering and search

```csharp
var results = context.Orders
    .WhereIf(includeCancelled is false, o => o.Status != OrderStatus.Cancelled)
    .WhereIfNotNull(minTotal, v => o => o.Total >= v)
    .WhereIn(context, o => o.Status, statuses)
    .WhereBetween(o => o.PlacedAt, from, to)
    .Search(OrderSearch, term);
```

- **`WhereIf`** returns the source itself when the condition is false, so an unapplied filter leaves
  the expression tree — and therefore the compiled-query cache key — exactly as it was.
- **`WhereIn`** with an empty set matches **nothing**, which is what SQL's `IN ()` means and the
  opposite of the "no filter" an empty list is sometimes assumed to mean. Use `WhereIf` to skip the
  filter entirely.
- **`WhereBetween`** is half-open, `[from, to)`, and either bound may be omitted. Written as
  `>= from && <= to`, a date range either drops the last day's rows or includes one instant of the
  next depending on whether the column carries a time. Half-open, consecutive ranges tile exactly.
- **`Search`** takes a `SearchSpecification<T>` — the same allowlist idea, over string fields. A blank
  term applies no filter, which is what an empty search box sends.
- **`Predicates.And` / `.Or` / `.Not`** splice predicate bodies and rebind the parameter, so the
  result is the tree you would have written by hand. Composing the delegates instead leaves an
  `Invoke` node EF cannot translate.

## Streaming

A whole result set in batches, driven by keyset paging rather than `Skip`/`Take`, so every batch costs
the same however deep it sits and a concurrent write cannot cause a row to be visited twice.

```csharp
await foreach (var batch in context.Orders.StreamBatchesAsync(context, ById, batchSize: 1_000, ct))
{
    await context.BulkUpdateAsync(Recalculate(batch), cancellationToken: ct);
}
```

`StreamAsync` yields rows instead of batches, reading a batch at a time underneath.

## Tracking scopes

```csharp
using (QueryTracking.NoTracking())
{
    await context.Orders.ToListAsync();          // not tracked

    using (QueryTracking.Tracking())
    {
        await context.Orders.ToListAsync();      // tracked — the innermost scope wins
    }

    await context.Orders.ToListAsync();          // not tracked again
}
```

The scope is ambient: it applies to every query on the same asynchronous flow, on any context
configured with `UseQueryHelpers()`. Scopes nest, and disposal walks past scopes already disposed, so
releasing one out of order neither drops a live inner scope nor resurrects a dead outer one.

An explicit `AsNoTracking()` or `AsTracking()` on the query itself beats the scope.

`context.BeginTrackingScope(behavior)` is the non-ambient counterpart: it changes one context rather
than one flow, needs no `UseQueryHelpers()` and replaces no EF service.

---

## Options

```csharp
options.UseNpgsql(connectionString).UseQueryHelpers(q => q
    .DefaultPageSize(25)                          // default 20
    .MaxPageSize(200)                             // default 100; a ceiling, applied by clamping
    .PageNumbering(PageNumbering.OneBased)        // default OneBased
    .CountStrategy(PageCountStrategy.TotalCount)  // default TotalCount
    .MaxOffsetRows(10_000)                        // advisory threshold; default 50,000
    .BatchSize(1_000)                             // streaming default; default 1,000
    .MaxInClauseValues(2_000)                     // advisory threshold; default 2,000
    .WithoutTrackingScopes()                      // leaves EF's IQueryContextFactory alone
    .Diagnostics(d => d
        .WarnOnMissingIndex()
        .WarnOnNonDeterministicOrder()
        .WarnOnDeepOffset()
        .WarnOnLargeInClause()
        .WarnOnEntityProjection()
        .WarnOnCollectionIncludeWithPaging()
        .OnWarning(QueryWarningBehavior.Throw)));
```

`DefaultPageSize` above `MaxPageSize` is refused at startup: every request that named no size would be
clamped to the ceiling, so the configured default would never reach a caller.

## Diagnostics

Everything under `Diagnostics` is off by default, and selecting checks is only half the switch — the
behaviour starts at `Ignore`, so nothing is reported until `OnWarning` says otherwise. With
diagnostics off the checks are not run at all, so an application that leaves them alone pays one
boolean per query.

| Check | Reports |
| --- | --- |
| `MissingIndex` | No declared index leads with the query's equality-filtered columns followed by its ordering columns, so the server sorts every matching row to answer a page. |
| `NonDeterministicOrder` | The ordering covers no key or unique index, so tied rows can appear on two pages or on neither. |
| `DeepOffset` | The page starts past `MaxOffsetRows`, so the server walks and discards everything before it. |
| `LargeInClause` | An `IN` list is longer than `MaxInClauseValues`. SQL Server caps a command at 2100 parameters and fails at execution, not at compile time. |
| `EntityProjection` | The page returns whole mapped entities rather than a projection. |
| `CollectionIncludeWithPaging` | The page spans a collection `Include` in a single query, so `Skip`/`Take` count joined rows rather than roots and the page comes back the wrong size. |

Findings are published to a `DiagnosticListener` named `EF.Toolkit.Query`, with one event per check —
`EF.Toolkit.Query.MissingIndex` and so on — carrying a `QueryAdvisoryEvent`. Under
`QueryWarningBehavior.Throw` every finding for a query is reported in one exception rather than the
first alone.

```csharp
if (listener.Name == QueryDiagnostics.ListenerName)
    listener.Subscribe(new AdvisoryObserver());
```

`Throw` belongs in test suites and local development. Enabling it in production turns an advisory into
an outage.

---

## How it works

**Pagination and filtering** build ordinary LINQ expression trees. What reaches the provider is
indistinguishable from hand-written `OrderBy` / `Where` / `Skip` / `Take` — including the parameters:
boundary values are read off a field of a captured object rather than embedded as constants, which is
the shape the C# compiler gives a captured local, so the SQL is parameterised and the server's plan
cache holds one entry rather than one per cursor.

**Keyset comparisons** are built as the lexicographic OR-of-ANDs:

```
(a > a0) || (a == a0 && b > b0) || (a == a0 && b == b0 && c > c0)
```

SQL's row-value form `(a, b) > (a0, b0)` says the same thing in one comparison, but SQL Server does not
support it and EF translates it on neither engine. Types without comparison operators — `string`,
`Guid` — go through `IComparable<T>.CompareTo`, which EF turns back into a plain SQL comparison
against the column. That the resulting order may not be .NET's own does not matter: both sides of the
comparison and the `ORDER BY` are evaluated by the same database, so they agree with each other.

**Tracking scopes** replace EF's `IQueryContextFactory` and apply the ambient preference to
`ChangeTracker.QueryTrackingBehavior` before each query. That seam is chosen deliberately.
`QueryCompiler` creates the query context, *then* generates the compiled-query cache key — and the key
includes the tracking behaviour — so a scope applied there is seen by both the cache and the
compilation, and a tracked and an untracked execution of the same LINQ occupy different cache entries.
An `IQueryExpressionInterceptor` runs only on a cache miss, so a scope expressed there would be baked
into the first compilation and silently served to every later execution. `IQueryContextFactory` is also
the only candidate service that neither the SQL Server nor the Npgsql provider overrides, which is why
this package has no provider variants.

**The advisor** answers everything from `IEntityType` metadata and the query's own expression tree. It
never touches the database.

---

## Limitations

- **The advisor is model-only.** An index created by hand — outside migrations, or by a DBA after the
  fact — is invisible to it, so `MissingIndex` reports a *possible* missing index. It also reads the
  expression tree on a best-effort basis: anything it does not recognise it leaves out, so it
  under-reports rather than inventing findings.
- **`MissingIndex` does not consider index direction.** A descending ordering against an ascending
  index is reported as covered. Most engines can walk an index backwards; some plans will not.
- **Cursors are not signed.** See the note under [Cursors](#cursors).
- **String comparisons follow the column's collation**, not this library — for keyset ordering, for
  `Search`, and for `WhereBetween` over text. A case-insensitive collation makes `"a"` and `"A"` tie,
  which a keyset ordering treats as a genuine tie.
- **`Search` does not escape `%` or `_`.** EF builds the `LIKE` pattern from the parameter without
  escaping, so those characters in a term behave as wildcards. Strip or escape them first if that
  matters.
- **Keyset paging over a projection cannot be model-checked.** Paging a DTO is supported, but the
  nullable, value-converter and uniqueness refusals need a mapped entity type to check against and are
  skipped when there is none.
- **Tracking scopes do not cross work that outlives the `using`**, do not affect `SaveChanges`, and
  apply only to contexts configured with `UseQueryHelpers()`. If something else in the application
  also replaces `IQueryContextFactory`, the last `ReplaceService` wins — call `WithoutTrackingScopes()`
  rather than letting one of the two silently stop working.
- **Streaming is not a snapshot.** Each batch is a separate query, so rows committed after the walk
  started and past the boundary are visited. Run it inside a transaction with a repeatable-read or
  snapshot isolation level if that matters.
- **`SortDirection` collides with Shouldly's type of the same name** in test projects that import
  both. A `using` alias resolves it.

---

## Sample

```bash
dotnet run --project samples/EF.Toolkit.Query.Sample
```

Starts its own PostgreSQL in Docker, so it needs no setup. Pass a connection string to point it at an
existing database instead.

## License

MIT
