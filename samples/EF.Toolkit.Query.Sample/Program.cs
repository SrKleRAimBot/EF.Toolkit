using System.Diagnostics;
using EFToolkit.Query;
using EFToolkit.Query.Configuration;
using EFToolkit.Query.Diagnostics;
using EFToolkit.Query.Filtering;
using EFToolkit.Query.Paging;
using EFToolkit.Query.Sample;
using EFToolkit.Query.Sorting;
using EFToolkit.Query.Tracking;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

// Runs with no setup: pass a PostgreSQL connection string, or let it start one in Docker.
//
//   dotnet run
//   dotnet run -- "Host=localhost;Database=shop;Username=postgres;Password=postgres"

PostgreSqlContainer? container = null;
string connectionString;

if (args.Length > 0)
{
    connectionString = args[0];
}
else
{
    Console.WriteLine("Starting PostgreSQL in Docker (pass a connection string to skip this)...");
    container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    await container.StartAsync();
    connectionString = container.GetConnectionString();
}

try
{
    await RunAsync(connectionString);
}
finally
{
    if (container is not null)
    {
        await container.DisposeAsync();
    }
}

static async Task RunAsync(string connectionString)
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Setup. One call, after the provider. Everything has a default; what is set
    // here is what a real application would most often want to change.
    // ─────────────────────────────────────────────────────────────────────────────
    var options = new DbContextOptionsBuilder<ShopContext>()
        .UseNpgsql(connectionString)
        .UseQueryHelpers(q => q
            .DefaultPageSize(10)
            .MaxPageSize(50)

            // Off in production. Section 6 turns it on for one context to show what it finds.
            .Diagnostics(d => d.WarnOnEverything().OnWarning(QueryWarningBehavior.Diagnostic)))
        .Options;

    await using (var setup = new ShopContext(options))
    {
        await setup.Database.EnsureCreatedAsync();
        await setup.Orders.ExecuteDeleteAsync();
        await setup.Customers.ExecuteDeleteAsync();
        await SeedAsync(setup);
    }

    await OffsetPaging(options);
    await KeysetPaging(options);
    await DeepPaging(options);
    await SortingAndFiltering(options);
    await Streaming(options);
    await Advisor(options);
    await TrackingScopes(options);

    Console.WriteLine();
    Console.WriteLine("Done.");
}

// ─────────────────────────────────────────────────────────────────────────────
// 1. Offset pagination — the shape for a numbered-page interface.
// ─────────────────────────────────────────────────────────────────────────────
static async Task OffsetPaging(DbContextOptions<ShopContext> options)
{
    Output.Section("1. Offset pagination");

    await using var context = new ShopContext(options);

    var page = await context.Orders
        .Where(o => o.Total > 20)
        .OrderBy(o => o.PlacedAt).ThenBy(o => o.Id)
        .Select(o => new { o.Id, o.Reference, o.Total })
        .ToPagedResultAsync(context, PageRequest.Of(2, 5));

    Console.WriteLine($"  page {page.PageNumber} of {page.TotalPages}, {page.TotalCount} rows in total");
    Console.WriteLine($"  hasPrevious={page.HasPrevious} hasNext={page.HasNext}");

    foreach (var row in page.Items)
    {
        Console.WriteLine($"    #{row.Id,-4} {row.Reference,-10} {row.Total,8:N2}");
    }

    // A page size above the configured ceiling is clamped, not refused — the value usually comes
    // straight off a query string, and a ceiling that threw would just be a way to generate 500s.
    var clamped = await context.Orders.OrderBy(o => o.Id).ToPagedResultAsync(
        context, PageRequest.Of(1, 1_000_000));

    Console.WriteLine($"  asked for 1,000,000 rows, got {clamped.PageSize} (MaxPageSize)");
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Keyset pagination — the shape for infinite scroll and for anything deep.
// ─────────────────────────────────────────────────────────────────────────────
static async Task KeysetPaging(DbContextOptions<ShopContext> options)
{
    Output.Section("2. Keyset pagination");

    await using var context = new ShopContext(options);

    // Ends in a unique column. Without one the ordering is partial, and this is refused rather than
    // silently returning a row on two pages or on none.
    var byNewest = KeysetDefinition.For<Order>(k => k
        .Descending(o => o.PlacedAt)
        .Ascending(o => o.Id));

    var first = await context.Orders.ToKeysetPageAsync(context, byNewest, pageSize: 4);

    Console.WriteLine($"  page 1: {string.Join(", ", first.Items.Select(o => o.Reference))}");
    Console.WriteLine($"  next cursor: {Output.Abbreviate(first.Next?.Token)}");

    var second = await context.Orders.ToKeysetPageAsync(context, byNewest, 4, first.Next);
    Console.WriteLine($"  page 2: {string.Join(", ", second.Items.Select(o => o.Reference))}");

    var back = await context.Orders.ToKeysetPageAsync(context, byNewest, 4, second.Previous);
    Console.WriteLine($"  back:   {string.Join(", ", back.Items.Select(o => o.Reference))}");

    // A cursor issued for one ordering carries a fingerprint of it, so replaying it against another
    // is refused rather than answered with an arbitrary page.
    var byTotal = KeysetDefinition.For<Order>(k => k
        .Descending(o => o.Total)
        .Ascending(o => o.Id));

    try
    {
        await context.Orders.ToKeysetPageAsync(context, byTotal, 4, first.Next);
    }
    catch (QueryNotSupportedException ex)
    {
        Console.WriteLine($"  replaying that cursor against a different sort: {Output.FirstSentence(ex.Message)}");
    }

    // A nullable ordering column is refused too: engines disagree about where NULL sorts, and a
    // comparison against it is neither true nor false, so those rows would be skipped by every page.
    try
    {
        KeysetDefinition.For<Order>(k => k.Ascending(o => (DateTime?)o.PlacedAt));
    }
    catch (QueryNotSupportedException ex)
    {
        Console.WriteLine($"  ordering by a nullable column:  {Output.FirstSentence(ex.Message)}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Why keyset paging exists — the cost of an offset grows with its depth.
// ─────────────────────────────────────────────────────────────────────────────
static async Task DeepPaging(DbContextOptions<ShopContext> options)
{
    Output.Section("3. Offset versus keyset, deep in the set");

    await using var context = new ShopContext(options);

    var total = await context.Orders.CountAsync();
    var lastPage = (total / 10) - 1;

    var byOldest = KeysetDefinition.For<Order>(k => k
        .Ascending(o => o.PlacedAt)
        .Ascending(o => o.Id));

    var offsetElapsed = await Output.Time(async () =>
    {
        await context.Orders
            .OrderBy(o => o.PlacedAt).ThenBy(o => o.Id)
            .ToPagedResultAsync(context, PageRequest.Of(lastPage, 10));
    });

    // Walk to the same depth by cursor, then time the page itself.
    KeysetCursor? cursor = null;
    for (var i = 0; i < lastPage - 1; i++)
    {
        var walked = await context.Orders.ToKeysetPageAsync(context, byOldest, 10, cursor);
        cursor = walked.Next;
    }

    var keysetElapsed = await Output.Time(async () =>
    {
        await context.Orders.ToKeysetPageAsync(context, byOldest, 10, cursor);
    });

    Console.WriteLine($"  page {lastPage} by offset: {offsetElapsed.TotalMilliseconds,6:N1} ms");
    Console.WriteLine($"  the same page by cursor: {keysetElapsed.TotalMilliseconds,6:N1} ms");
    Console.WriteLine("  the gap widens with depth: the offset walks and discards everything before it,");
    Console.WriteLine("  while the cursor seeks straight to the boundary.");
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. Sorting contracts and composable filters.
// ─────────────────────────────────────────────────────────────────────────────
static async Task SortingAndFiltering(DbContextOptions<ShopContext> options)
{
    Output.Section("4. Sorting and filtering");

    await using var context = new ShopContext(options);

    // An allowlist. The caller supplies a field name, never an expression, so an API-supplied sort
    // cannot reach a column this query did not offer.
    var sort = SortSpecification.For<Order>(s => s
        .Allow("placed", o => o.PlacedAt)
        .Allow("total", o => o.Total)
        .Allow("reference", o => o.Reference)
        .DefaultOrder("placed", SortDirection.Descending)
        .Tiebreaker(o => o.Id));

    var search = SearchSpecification.For<Order>(s => s.Field(o => o.Reference));

    decimal? minimumTotal = 20m;
    string? term = "REF-0";

    var page = await context.Orders
        .WhereIf(condition: true, o => o.Status != OrderStatus.Cancelled)
        .WhereIfNotNull(minimumTotal, v => o => o.Total >= v)
        .WhereIn(context, o => o.Status, [OrderStatus.Placed, OrderStatus.Shipped])
        .Search(search, term)
        .OrderBy(sort, "total:desc,placed")
        .ToPagedResultAsync(context, PageRequest.Of(1, 5));

    Console.WriteLine($"  {page.TotalCount} matching, showing {page.Items.Count}");

    foreach (var row in page.Items)
    {
        Console.WriteLine($"    {row.Reference,-10} {row.Total,8:N2}  {row.Status}");
    }

    // An unknown field is refused, and the message says what is allowed. Skipping it would return
    // rows in an order nobody asked for, with nothing in the response to notice it by.
    try
    {
        context.Orders.OrderBy(sort, "customer_secret_field");
    }
    catch (QueryNotSupportedException ex)
    {
        Console.WriteLine($"  sorting by an unlisted field: {Output.FirstSentence(ex.Message)}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. Streaming a whole set in batches, without holding it in memory.
// ─────────────────────────────────────────────────────────────────────────────
static async Task Streaming(DbContextOptions<ShopContext> options)
{
    Output.Section("5. Batched streaming");

    await using var context = new ShopContext(options);

    var byId = KeysetDefinition.For<Order>(k => k.Ascending(o => o.Id));

    var batches = 0;
    var rows = 0;
    decimal revenue = 0;

    await foreach (var batch in context.Orders.StreamBatchesAsync(context, byId, batchSize: 25))
    {
        batches++;
        rows += batch.Count;
        revenue += batch.Sum(o => o.Total);
    }

    Console.WriteLine($"  visited {rows} rows in {batches} batches of at most 25");
    Console.WriteLine($"  total revenue: {revenue:N2}");
    Console.WriteLine("  driven by cursors, so each batch costs the same however deep it sits, and a");
    Console.WriteLine("  concurrent insert cannot shift the boundary and cause a row to be seen twice.");
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. The advisor — model-only, no database round trip, off by default.
// ─────────────────────────────────────────────────────────────────────────────
static async Task Advisor(DbContextOptions<ShopContext> options)
{
    Output.Section("6. Development-time advisor");

    using var listener = new AdvisoryPrinter();
    await using var context = new ShopContext(options);

    // Ordered by Total, which nothing indexes, and returning whole entities rather than a projection.
    await context.Orders
        .OrderBy(o => o.Total)
        .ToPagedResultAsync(context, PageRequest.Of(1, 5));

    Console.WriteLine("  every finding comes from the EF model and the query's own expression tree;");
    Console.WriteLine("  the database was never asked.");
}

// ─────────────────────────────────────────────────────────────────────────────
// 7. Ambient tracking scopes.
// ─────────────────────────────────────────────────────────────────────────────
static async Task TrackingScopes(DbContextOptions<ShopContext> options)
{
    Output.Section("7. Tracking scopes");

    await using var context = new ShopContext(options);

    await context.Orders.Take(5).ToListAsync();
    Console.WriteLine($"  no scope:              {context.ChangeTracker.Entries<Order>().Count()} tracked");

    context.ChangeTracker.Clear();

    using (QueryTracking.NoTracking())
    {
        await context.Orders.Take(5).ToListAsync();
        Console.WriteLine($"  inside NoTracking():   {context.ChangeTracker.Entries<Order>().Count()} tracked");

        using (QueryTracking.Tracking())
        {
            await context.Orders.Take(5).ToListAsync();
            Console.WriteLine($"    inside Tracking():   {context.ChangeTracker.Entries<Order>().Count()} tracked");
        }
    }

    context.ChangeTracker.Clear();
    await context.Orders.Take(5).ToListAsync();
    Console.WriteLine($"  after the scope closed: {context.ChangeTracker.Entries<Order>().Count()} tracked");
    Console.WriteLine("  the same LINQ ran tracked and untracked: the scope is applied before EF's");
    Console.WriteLine("  compiled-query cache key, so the two do not share a cached plan.");
}

static async Task SeedAsync(ShopContext context)
{
    var customers = Enumerable.Range(1, 5)
        .Select(i => new Customer { Name = $"Customer {i:D2}", Email = $"c{i}@example.com" })
        .ToArray();

    context.Customers.AddRange(customers);
    await context.SaveChangesAsync();

    var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Deliberately repetitive: five orders share each date, so the tiebreaker and the lexicographic
    // keyset comparison both have ties to resolve rather than a set of conveniently distinct keys.
    var orders = Enumerable.Range(0, 200).Select(i => new Order
    {
        PlacedAt = epoch.AddDays(i / 5),
        Total = Output.SeedTotals[i % Output.SeedTotals.Length],
        Status = (OrderStatus)(i % 3),
        CustomerId = customers[i % customers.Length].Id,
        Reference = $"REF-{i:D4}",
    });

    context.Orders.AddRange(orders);
    await context.SaveChangesAsync();
}

/// <summary>Prints whatever the advisor publishes while it is alive.</summary>
internal sealed class AdvisoryPrinter : IDisposable, IObserver<DiagnosticListener>
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly IDisposable _allListeners;

    public AdvisoryPrinter() => _allListeners = DiagnosticListener.AllListeners.Subscribe(this);

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == QueryDiagnostics.ListenerName)
        {
            _subscriptions.Add(value.Subscribe(new EventPrinter()));
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _allListeners.Dispose();
    }

    private sealed class EventPrinter : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is QueryAdvisoryEvent published)
            {
                Console.WriteLine($"  [{published.Advisory.Check}] {published.Advisory.Message}");
                Console.WriteLine();
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
