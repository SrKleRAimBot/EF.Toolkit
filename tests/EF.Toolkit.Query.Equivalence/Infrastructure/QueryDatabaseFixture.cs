using EFToolkit.Query.Configuration;
using EFToolkit.Query.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFToolkit.Query.Equivalence.Infrastructure;

/// <summary>One database on one engine, reset between scenarios.</summary>
/// <remarks>
///     Unlike the bulk and audit suites there is no second write path to compare against — a query
///     package changes nothing about how rows are written. What these suites compare instead is one
///     paging strategy against another, and both against the ordering itself, so what needs to be
///     identical across engines is only the data each scenario starts from.
/// </remarks>
public abstract class QueryDatabaseFixture : IAsyncLifetime
{
    /// <summary>Value of the <c>Engine</c> trait, used to shard the suite in CI.</summary>
    public abstract string Engine { get; }

    /// <summary>Whether the EF Core provider can order by a <see cref="DateTimeOffset" /> column.</summary>
    /// <remarks>
    ///     SQLite's cannot: it stores the value as text, and refuses to translate an ordering that text
    ///     comparison would get wrong across offsets. There is no keyset to walk without an ordering.
    /// </remarks>
    public virtual bool OrdersByDateTimeOffset => true;

    /// <summary>Set when the engine cannot run here; the suite skips rather than fails.</summary>
    public string? SkipReason { get; protected set; }

    /// <summary>Connection string for the database under test.</summary>
    protected string ConnectionString { get; private set; } = "";

    /// <summary>Starts the container and creates the schema.</summary>
    public async ValueTask InitializeAsync()
    {
        SkipReason = await CheckPrerequisitesAsync();
        if (SkipReason is not null)
        {
            return;
        }

        await StartContainerAsync();

        if (SkipReason is not null)
        {
            return;
        }

        ConnectionString = await CreateDatabaseAsync("efquery");

        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>Empties every table and resets key generation.</summary>
    public abstract Task ResetAsync();

    /// <summary>A context with EF.Toolkit.Query configured.</summary>
    /// <param name="configure">Extra query settings.</param>
    /// <param name="queryHelpers">
    ///     Whether to call <c>UseQueryHelpers()</c> at all, so the unconfigured-context path can be
    ///     exercised against a real database rather than only in a unit test.
    /// </param>
    public ShopContext CreateContext(
        Action<QueryOptionsBuilder>? configure = null,
        bool queryHelpers = true)
    {
        // Every engine and every configuration a scenario varies gets an internal service provider
        // of its own, and with five engines in one process that passes the twenty at which EF
        // starts treating the count as a mistake. It is not one here: each is cached and reused.
        var builder = new DbContextOptionsBuilder<ShopContext>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        ConfigureProvider(builder, ConnectionString);

        if (queryHelpers)
        {
            builder.UseQueryHelpers(configure);
        }

        return new ShopContext(builder.Options);
    }

    /// <summary>The mapped tables of the test model, as (schema, name) pairs.</summary>
    protected IReadOnlyList<(string? Schema, string Name)> MappedTables()
    {
        using var context = CreateContext();

        return context.Model.GetEntityTypes()
            .SelectMany(e => e.GetTableMappings())
            .Select(m => (m.Table.Schema, m.Table.Name))
            .Distinct()
            .ToList();
    }

    /// <summary>Returns a skip reason when this engine cannot run in the current environment.</summary>
    protected virtual ValueTask<string?> CheckPrerequisitesAsync()
        => ValueTask.FromResult<string?>(null);

    /// <summary>Starts the database container.</summary>
    protected abstract Task StartContainerAsync();

    /// <summary>Creates a database and returns a connection string pointing at it.</summary>
    protected abstract Task<string> CreateDatabaseAsync(string databaseName);

    /// <summary>Applies the EF Core provider.</summary>
    protected abstract void ConfigureProvider(
        DbContextOptionsBuilder<ShopContext> builder,
        string connectionString);
}
