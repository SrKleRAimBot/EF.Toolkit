using System.Diagnostics;
using EFToolkit.Bulk.Sample;
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
    container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
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
    // Setup. One call, after the provider. Everything else follows from this.
    // ─────────────────────────────────────────────────────────────────────────────
    var options = new DbContextOptionsBuilder<ShopContext>()
        .UseNpgsql(connectionString)
        .UseBulkOperations()
        .Options;

    await using (var setup = new ShopContext(options))
    {
        await setup.Database.EnsureCreatedAsync();

        // Emptied rather than dropped: the connection string may well point at the database we
        // are connected to, and PostgreSQL will not drop that. Orders first, for the foreign key.
        await setup.Orders.ExecuteDeleteAsync();
        await setup.Customers.ExecuteDeleteAsync();
    }

    await TransparentSaveChanges(options);
    await ExplicitInsert(options);
    await UpdateAndDelete(options);
    await Upsert(options);
    await Synchronize(options);
    await WholeGraph(options);

    Console.WriteLine();
    Console.WriteLine("Done.");
}

// ─────────────────────────────────────────────────────────────────────────────────
// 1. Transparent: existing SaveChanges() code is accelerated with no change at all.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task TransparentSaveChanges(DbContextOptions<ShopContext> options)
{
    Section("Transparent SaveChanges()");

    await using var context = new ShopContext(options);
    context.Customers.AddRange(Customers(0, 20_000));

    var elapsed = await Time(() => context.SaveChangesAsync());

    Console.WriteLine($"  inserted 20,000 customers in {elapsed.TotalMilliseconds:F0} ms");
    Console.WriteLine("  no call-site change; keys populated and entities left Unchanged as usual");
}

// ─────────────────────────────────────────────────────────────────────────────────
// 2. Explicit: skips EF's change tracking and command pipeline entirely.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task ExplicitInsert(DbContextOptions<ShopContext> options)
{
    Section("BulkInsertAsync");

    await using var context = new ShopContext(options);
    var customers = Customers(20_000, 80_000);

    var elapsed = await Time(() => context.BulkInsertAsync(customers));

    Console.WriteLine($"  inserted 80,000 customers in {elapsed.TotalMilliseconds:F0} ms");
    Console.WriteLine($"  keys are populated even though nothing is tracked: "
        + $"Id={customers[0].Id}, State={context.Entry(customers[0]).State}");
}

// ─────────────────────────────────────────────────────────────────────────────────
// 3. Update and delete, as single set-based statements.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task UpdateAndDelete(DbContextOptions<ShopContext> options)
{
    Section("BulkUpdateAsync / BulkDeleteAsync");

    await using var context = new ShopContext(options);

    var customers = await context.Customers.AsNoTracking().Take(50_000).ToListAsync();
    foreach (var customer in customers)
    {
        customer.Balance += 10m;
    }

    // Every non-key column is written: there is no change tracking here to say which ones moved.
    var updated = await context.BulkUpdateAsync(customers);
    Console.WriteLine($"  updated {updated.Updated:N0} customers");

    var deleted = await context.BulkDeleteAsync(customers.Take(10_000).ToList());
    Console.WriteLine($"  deleted {deleted.Deleted:N0} customers");
}

// ─────────────────────────────────────────────────────────────────────────────────
// 4. Upsert. The database decides insert-versus-update per row.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task Upsert(DbContextOptions<ShopContext> options)
{
    Section("BulkMergeAsync");

    await ResetTo(options, startAt: 0, count: 10_000);

    await using var context = new ShopContext(options);

    // Half of these emails already exist and half do not, so both arms of the upsert run.
    var incoming = Customers(5_000, 10_000);

    var result = await context.BulkMergeAsync(incoming, o => o.MatchOn(c => c.Email));

    Console.WriteLine($"  {result.Inserted:N0} inserted, {result.Updated:N0} updated");
    Console.WriteLine("  one statement, so no window for another writer to slip in");
}

// ─────────────────────────────────────────────────────────────────────────────────
// 5. Synchronize: make the table match a list exactly, deletes included.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task Synchronize(DbContextOptions<ShopContext> options)
{
    Section("BulkSynchronizeAsync");

    await ResetTo(options, startAt: 0, count: 10_000);

    await using var context = new ShopContext(options);

    // Overlaps the existing rows by half, so this inserts, updates and deletes 5,000 each.
    var desired = Customers(5_000, 10_000);
    var result = await context.BulkSynchronizeAsync(
        desired, o => o.MatchOn(c => c.Email).AllowFullTableDelete());

    Console.WriteLine(
        $"  {result.Inserted:N0} inserted, {result.Updated:N0} updated, {result.Deleted:N0} deleted");
    Console.WriteLine("  the delete covers the whole table — that is what makes it a synchronise");

    var remaining = await context.Customers.CountAsync();
    Console.WriteLine($"  table now holds {remaining:N0} rows");
}

// ─────────────────────────────────────────────────────────────────────────────────
// 6. IncludeGraph: navigations followed, principals first, foreign keys filled in.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task WholeGraph(DbContextOptions<ShopContext> options)
{
    Section("BulkInsertAsync with IncludeGraph()");

    await using var context = new ShopContext(options);

    var customers = Customers(100_000, 10_000);
    foreach (var customer in customers)
    {
        for (var i = 1; i <= 3; i++)
        {
            // Note that CustomerId is never assigned — the navigation is enough.
            customer.Orders.Add(new Order
            {
                Customer = customer,
                Reference = $"{customer.Email}-{i}",
                Total = 25m * i
            });
        }
    }

    var elapsed = await Time(
        () => context.BulkInsertAsync(customers, o => o.IncludeGraph()));

    Console.WriteLine($"  inserted 10,000 customers and 30,000 orders in {elapsed.TotalMilliseconds:F0} ms");
    Console.WriteLine(
        $"  foreign keys were filled from the navigations: "
        + $"Customer {customers[0].Id} -> Order.CustomerId {customers[0].Orders[0].CustomerId}");
}

/// <summary>Empties the tables and seeds a known set, so each section stands on its own.</summary>
static async Task ResetTo(DbContextOptions<ShopContext> options, int startAt, int count)
{
    await using var context = new ShopContext(options);

    await context.Orders.ExecuteDeleteAsync();
    await context.Customers.ExecuteDeleteAsync();

    await context.BulkInsertAsync(Customers(startAt, count));
}

static List<Customer> Customers(int startAt, int count)
    =>
    [
        .. Enumerable.Range(startAt, count).Select(i => new Customer
        {
            Name = $"Customer {i}",
            Email = $"customer{i}@example.com",
            Balance = i % 500
        })
    ];

static async Task<TimeSpan> Time(Func<Task> action)
{
    var stopwatch = Stopwatch.StartNew();
    await action();
    stopwatch.Stop();
    return stopwatch.Elapsed;
}

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine($"── {title} ".PadRight(78, '─'));
}
