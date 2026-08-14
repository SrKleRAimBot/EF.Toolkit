using EFToolkit.Audit.Api;
using EFToolkit.Audit.Sample;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
    // Setup. One call per capability, after the provider.
    //
    // UseBulkAuditing() is what joins the two packages: neither references the other,
    // and without it the explicit bulk API would write rows nothing records.
    // ─────────────────────────────────────────────────────────────────────────────
    var options = new DbContextOptionsBuilder<ShopContext>()
        .UseNpgsql(connectionString)
        .UseBulkOperations()
        .UseAuditing(a => a
            .Schema("audit")

            // Reads the tenant off each audited entity, shadow properties included. This is the
            // whole Finbuckle.MultiTenant integration.
            .MultiTenant(t => t.FromEntityProperty())

            // Bring your own identifier scheme. The default is a client-generated UUIDv7.
            .Ids(_ => $"aud_{Guid.CreateVersion7():N}"))
        .UseBulkAuditing()
        .Options;

    await using (var setup = new ShopContext(options))
    {
        await setup.Database.EnsureCreatedAsync();
        await setup.Products.ExecuteDeleteAsync();
    }

    await ThroughSaveChanges(options);
    await ThroughTheBulkApi(options);
    await NotAudited(options);
    await Print(connectionString);
    await QueryThePayload(connectionString);

    Console.WriteLine();
    Console.WriteLine("Done.");
}

// ─────────────────────────────────────────────────────────────────────────────────
// 1. SaveChanges. An insert, then an update, under a scope that says who and why.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task ThroughSaveChanges(DbContextOptions<ShopContext> options)
{
    Console.WriteLine();
    Console.WriteLine("── SaveChanges ─────────────────────────────────────────────");

    using var scope = AuditScope.Begin(
        new AuditActor("u-42", "Ada Lovelace", "user"),
        reason: "initial load");

    await using var context = new ShopContext(options);

    context.Products.AddRange(
        new Product
        {
            Sku = "SKU-1",
            Name = "Widget",
            Price = 9.99m,
            TenantId = "acme",
            SupplierAccount = "ACC-77771234",
            ScratchNotes = "check packaging",
        },
        new Product { Sku = "SKU-2", Name = "Gadget", Price = 19.99m, TenantId = "acme" });

    await context.SaveChangesAsync();
    Console.WriteLine("  inserted 2 products");

    var widget = await context.Products.SingleAsync(p => p.Sku == "SKU-1");
    widget.Status = ProductStatus.Live;
    widget.Price = 11.49m;

    await context.SaveChangesAsync();
    Console.WriteLine("  updated 1 product");
}

// ─────────────────────────────────────────────────────────────────────────────────
// 2. The explicit bulk API. It bypasses the change tracker, so nothing a SaveChanges
//    interceptor can see happens here at all — and it is audited identically.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task ThroughTheBulkApi(DbContextOptions<ShopContext> options)
{
    Console.WriteLine();
    Console.WriteLine("── BulkUpdateAsync ─────────────────────────────────────────");

    using var scope = AuditScope.Begin(actor: "reprice-job", reason: "quarterly reprice")
        .With("run", 7);

    await using var context = new ShopContext(options);

    // Detached objects: no before-image on them anywhere. The old values in the trail come from
    // a read of the affected rows, issued inside the operation's own transaction.
    var products = await context.Products.AsNoTracking().ToListAsync();

    foreach (var product in products)
    {
        product.Price *= 1.10m;
    }

    await context.BulkUpdateAsync(products);
    Console.WriteLine($"  repriced {products.Count} products");
}

// ─────────────────────────────────────────────────────────────────────────────────
// 3. An unregistered type. Auditing is opt-in, and stays that way.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task NotAudited(DbContextOptions<ShopContext> options)
{
    await using var context = new ShopContext(options);

    context.Sessions.Add(new Session { Token = "not-recorded" });
    await context.SaveChangesAsync();
}

// ─────────────────────────────────────────────────────────────────────────────────
// 4. The trail. Both write paths, side by side.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task Print(string connectionString)
{
    Console.WriteLine();
    Console.WriteLine("── audit.\"AuditEntries\" ────────────────────────────────────");

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();

    command.CommandText =
        """
        SELECT "Source", "EntityType", "EntityKey", "Operation", "ActorId", "TenantId", "Changes"
        FROM audit."AuditEntries"
        ORDER BY "OccurredAt", "EntityKey"
        """;

    await using var reader = await command.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        var operation = (AuditOperation)reader.GetInt32(3);

        Console.WriteLine();
        Console.WriteLine(
            $"  {reader.GetString(0),-14} {reader.GetString(1)}#{reader.GetString(2)}  "
            + $"{operation}  actor={reader.GetString(4)}  tenant={reader.GetString(5)}");

        Console.WriteLine($"    {reader.GetString(6)}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────────
// 5. Why jsonb. The payload is searchable by what actually changed, through the GIN
//    index — not merely readable.
// ─────────────────────────────────────────────────────────────────────────────────
static async Task QueryThePayload(string connectionString)
{
    Console.WriteLine();
    Console.WriteLine("── querying the payload ────────────────────────────────────");

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await Count(
        connection,
        "everything that went Live",
        """SELECT count(*) FROM audit."AuditEntries" WHERE "Changes" @> '{"new":{"Status":"Live"}}'""");

    await Count(
        connection,
        "everything whose Price moved",
        """SELECT count(*) FROM audit."AuditEntries" WHERE "Changes" -> 'changed' ? 'Price'""");

    static async Task Count(NpgsqlConnection connection, string label, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        Console.WriteLine($"  {label,-32} {await command.ExecuteScalarAsync()}");
    }
}
