using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Equivalence.Infrastructure;

/// <summary>
///     One database on one engine, with auditing configured, reset between scenarios.
/// </summary>
/// <remarks>
///     One database rather than two. The comparison this suite makes is between the same change
///     written two different ways, and running those in turn against a database reset in between
///     puts them on identical footing — same key values, same starting state — without the second
///     database that a side-by-side comparison would need.
/// </remarks>
public abstract class AuditDatabaseFixture : IAsyncLifetime
{
    /// <summary>Value of the <c>Engine</c> trait, used to shard the suite in CI.</summary>
    public abstract string Engine { get; }

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

        ConnectionString = await CreateDatabaseAsync("efaudit");

        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>Empties every table, audit table included, and resets key generation.</summary>
    public abstract Task ResetAsync();

    /// <summary>A context with auditing configured, and optionally the bulk packages.</summary>
    /// <param name="configure">Extra auditing settings.</param>
    /// <param name="bulk">Whether to add <c>UseBulkOperations()</c> and <c>UseBulkAuditing()</c>.</param>
    /// <param name="auditing">Whether to configure auditing at all.</param>
    /// <param name="applicationServices">
    ///     Stands in for the container an application would register the context with, for the
    ///     settings that resolve from it — a custom sink, an actor provider.
    /// </param>
    public ShopContext CreateContext(
        Action<AuditOptionsBuilder>? configure = null,
        bool bulk = false,
        bool auditing = true,
        IServiceProvider? applicationServices = null)
    {
        var builder = new DbContextOptionsBuilder<ShopContext>()
            .UseApplicationServiceProvider(applicationServices);

        ConfigureProvider(builder, ConnectionString);

        if (bulk)
        {
            ConfigureBulk(builder);
        }

        if (auditing)
        {
            ConfigureAuditing(
                builder,
                a =>
                {
                    a.UseTimeProvider(FixedTimeProvider.Default);
                    a.MultiTenant(t => t.FromEntityProperty());
                    configure?.Invoke(a);
                });

            if (bulk)
            {
                builder.UseBulkAuditing();
            }
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

    /// <summary>Applies the provider's <c>UseAuditing()</c>.</summary>
    protected abstract void ConfigureAuditing(
        DbContextOptionsBuilder<ShopContext> builder,
        Action<AuditOptionsBuilder> configure);

    /// <summary>Applies the provider's <c>UseBulkOperations()</c>.</summary>
    protected abstract void ConfigureBulk(DbContextOptionsBuilder<ShopContext> builder);

    /// <summary>Opens a raw connection, for reading rows without going through EF.</summary>
    public abstract System.Data.Common.DbConnection OpenConnection();

    /// <summary>Quotes an identifier the way this engine does.</summary>
    /// <remarks>
    ///     The audit rows are read over ADO rather than through EF, so that a mapping which is
    ///     wrong in the same way in both directions — a JSON column that round-trips through a
    ///     converter, say — cannot hide behind itself.
    /// </remarks>
    public abstract string Quote(string identifier);
}
