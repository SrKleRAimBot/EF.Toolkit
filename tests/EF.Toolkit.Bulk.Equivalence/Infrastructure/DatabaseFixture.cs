using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Equivalence.Infrastructure;

/// <summary>
///     Provides two structurally identical databases on one engine — one reached through stock EF
///     Core, one through EF.Toolkit.Bulk — so a scenario can be run twice and the results compared.
/// </summary>
/// <remarks>
///     Both databases live in a single container. Isolating them as separate databases rather than
///     separate schemas keeps identity/sequence state independent, which matters because key
///     allocation is one of the things under test.
/// </remarks>
public abstract class DatabaseFixture : IAsyncLifetime
{
    /// <summary>Value of the <c>Engine</c> trait, used to shard the suite in CI.</summary>
    public abstract string Engine { get; }

    /// <summary>Connection string for the database written through stock EF Core.</summary>
    protected string StockConnectionString { get; private set; } = "";

    /// <summary>Connection string for the database written through EF.Toolkit.Bulk.</summary>
    protected string BulkConnectionString { get; private set; } = "";

    /// <summary>Set when the engine cannot run here; the suite skips rather than fails.</summary>
    public string? SkipReason { get; protected set; }

    /// <summary>Starts the container and creates both databases.</summary>
    public async ValueTask InitializeAsync()
    {
        SkipReason = await CheckPrerequisitesAsync();
        if (SkipReason is not null)
        {
            return;
        }

        await StartContainerAsync();

        // StartContainerAsync may itself decide the engine is unavailable here.
        if (SkipReason is not null)
        {
            return;
        }

        StockConnectionString = await CreateDatabaseAsync("efbulk_stock");
        BulkConnectionString = await CreateDatabaseAsync("efbulk_bulk");

        await using (var stock = CreateStockContext())
        {
            await stock.Database.EnsureCreatedAsync();
        }

        await using var bulk = CreateBulkContext();
        await bulk.Database.EnsureCreatedAsync();
    }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>
    ///     Empties both databases and resets identity/sequence state.
    /// </summary>
    /// <remarks>
    ///     Resetting the key generators matters as much as deleting the rows: generated key values
    ///     are part of what the harness compares, so leftover identity state in one database would
    ///     show up as a spurious divergence.
    /// </remarks>
    public async Task ResetAsync()
    {
        await ResetAsync(StockConnectionString);
        await ResetAsync(BulkConnectionString);
    }

    /// <summary>Empties one database and resets its identity/sequence state.</summary>
    protected abstract Task ResetAsync(string connectionString);

    /// <summary>The mapped tables of the test model, as (schema, name) pairs.</summary>
    protected IReadOnlyList<(string? Schema, string Name)> MappedTables()
    {
        using var context = CreateStockContext();

        return context.Model.GetEntityTypes()
            .SelectMany(e => e.GetTableMappings())
            .Select(m => (m.Table.Schema, m.Table.Name))
            .Distinct()
            .ToList();
    }

    /// <summary>A context that writes through stock EF Core, with no EF.Toolkit.Bulk services.</summary>
    public ShopContext CreateStockContext()
    {
        var builder = new DbContextOptionsBuilder<ShopContext>();
        ConfigureProvider(builder, StockConnectionString, retryOnFailure: false);
        return new ShopContext(builder.Options);
    }

    /// <summary>A context that writes through EF.Toolkit.Bulk.</summary>
    /// <param name="configure">Optional context-wide EF.Toolkit.Bulk settings.</param>
    /// <param name="retryOnFailure">
    ///     Configures a retrying execution strategy. Such a strategy refuses a user-initiated
    ///     transaction unless the whole operation runs inside its retry loop, so it is the
    ///     configuration most likely to break the explicit API.
    /// </param>
    public ShopContext CreateBulkContext(
        Action<BulkOptionsBuilder>? configure = null,
        bool retryOnFailure = false)
    {
        var builder = new DbContextOptionsBuilder<ShopContext>();
        ConfigureProvider(builder, BulkConnectionString, retryOnFailure);
        ConfigureBulk(builder, configure);
        return new ShopContext(builder.Options);
    }

    /// <summary>Returns a skip reason when this engine cannot run in the current environment.</summary>
    protected virtual ValueTask<string?> CheckPrerequisitesAsync()
        => ValueTask.FromResult<string?>(null);

    /// <summary>Starts the database container.</summary>
    protected abstract Task StartContainerAsync();

    /// <summary>Creates a database and returns a connection string pointing at it.</summary>
    protected abstract Task<string> CreateDatabaseAsync(string databaseName);

    /// <summary>Applies the EF Core provider to <paramref name="builder" />.</summary>
    /// <param name="builder">The builder being configured.</param>
    /// <param name="connectionString">The database to point at.</param>
    /// <param name="retryOnFailure">Whether to configure a retrying execution strategy.</param>
    protected abstract void ConfigureProvider(
        DbContextOptionsBuilder<ShopContext> builder,
        string connectionString,
        bool retryOnFailure);

    /// <summary>Applies <c>UseBulkOperations()</c> to <paramref name="builder" />.</summary>
    /// <param name="builder">The builder being configured.</param>
    /// <param name="configure">Optional context-wide EF.Toolkit.Bulk settings.</param>
    protected abstract void ConfigureBulk(
        DbContextOptionsBuilder<ShopContext> builder,
        Action<BulkOptionsBuilder>? configure);
}
