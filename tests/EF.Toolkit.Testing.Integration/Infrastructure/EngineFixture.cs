using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFToolkit.Testing.Integration.Infrastructure;

/// <summary>One engine in a container, with a <see cref="TestDatabase{TContext}" /> on it.</summary>
public abstract class EngineFixture : IAsyncLifetime
{
    private TestDatabase<ShopContext>? _database;

    /// <summary>Value of the <c>Engine</c> trait, used to shard the suite in CI.</summary>
    public abstract string Engine { get; }

    /// <summary>Set when the engine cannot run here; the suite skips rather than fails.</summary>
    public string? SkipReason { get; protected set; }

    /// <summary>The database under test. Only valid when <see cref="SkipReason" /> is null.</summary>
    public TestDatabase<ShopContext> Database => _database ?? throw new InvalidOperationException(SkipReason);

    /// <summary>Connection string for the database under test.</summary>
    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        SkipReason = await CheckPrerequisitesAsync();
        if (SkipReason is not null)
        {
            return;
        }

        try
        {
            await StartContainerAsync();
        }
        catch (DockerUnavailableException ex)
        {
            SkipReason = $"Docker is not available: {ex.Message}";
        }

        if (SkipReason is not null)
        {
            return;
        }

        ConnectionString = ConnectionStringFor("eftesting");

        // DeleteExisting so the schema is the model's, whatever an earlier run left behind.
        _database = await TestDatabase<ShopContext>.CreateAsync(
            o =>
            {
                ConfigureProvider(o, ConnectionString);
                o.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            },
            o => o.DeleteExisting());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        await DisposeContainerAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A context on a connection of its own, outside any scope.</summary>
    public ShopContext CreateContext() => Database.CreateContext();

    /// <summary>Returns a skip reason when this engine cannot run in the current environment.</summary>
    protected virtual ValueTask<string?> CheckPrerequisitesAsync()
        => ValueTask.FromResult<string?>(
            DockerCli.IsAvailable() ? null : "Docker is not available, so the engine suites are skipped.");

    /// <summary>Starts the container.</summary>
    protected abstract Task StartContainerAsync();

    /// <summary>A connection string to a database of that name on the container.</summary>
    protected abstract string ConnectionStringFor(string databaseName);

    /// <summary>Applies the EF Core provider.</summary>
    protected abstract void ConfigureProvider(DbContextOptionsBuilder<ShopContext> builder, string connectionString);

    /// <summary>Stops the container.</summary>
    protected abstract ValueTask DisposeContainerAsync();
}

/// <summary>Whether a Docker daemon answers, checked once per run.</summary>
internal static class DockerCli
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            // Resolving the endpoint is what Testcontainers does first; it throws when none answers.
            return TestcontainersSettings.OS.DockerEndpointAuthConfig is not null;
        }
        catch (DockerUnavailableException)
        {
            return false;
        }
    });

    public static bool IsAvailable() => Available.Value;
}
