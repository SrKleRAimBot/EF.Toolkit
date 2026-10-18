using EFToolkit.Bulk.Execution;
using EFToolkit.Bulk.MySql.Execution;
using EFToolkit.Bulk.MySql.Infrastructure;
using EFToolkit.Bulk.Sqlite.Execution;
using EFToolkit.Bulk.Sqlite.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Configuration;

/// <summary>
///     What each provider package's <c>UseBulkOperations()</c> puts in EF's service provider.
/// </summary>
/// <remarks>
///     Resolving the services opens no connection, so none of this needs a database — which matters
///     most for MySQL, whose package reaches the provider's batch factory by decorating whatever it
///     finds registered rather than by naming a type, and so has nothing a compiler could check.
/// </remarks>
public class ProviderRegistrationTests
{
    [Fact]
    public void MySql_wraps_the_providers_own_batch_factory()
    {
        using var context = Context(o => o.UseMySQL("Server=localhost;Database=efbulk").UseMySqlBulk());

        context.GetService<IModificationCommandBatchFactory>()
            .ShouldBeOfType<MySqlBulkModificationCommandBatchFactory>();

        context.GetService<IBulkOperationExecutor>().ShouldBeOfType<MySqlBulkExecutor>();
    }

    [Fact]
    public void MySql_refuses_to_be_configured_before_the_provider()
    {
        // The decorator has nothing to wrap until the provider's services exist, and saying so —
        // as soon as the context is built — is better than leaving SaveChanges unaccelerated.
        Should.Throw<InvalidOperationException>(
                () => Context(o => o.UseMySqlBulk().UseMySQL("Server=localhost;Database=efbulk")))
            .Message.ShouldContain("before UseBulkOperations()");
    }

    [Fact]
    public void MySql_batches_fall_back_to_a_genuine_provider_batch()
    {
        using var context = Context(o => o.UseMySQL("Server=localhost;Database=efbulk").UseMySqlBulk());

        var batch = context.GetService<IModificationCommandBatchFactory>().Create();

        batch.ShouldBeOfType<EFToolkit.Bulk.Infrastructure.BulkModificationCommandBatch>();
    }

    [Fact]
    public void Sqlite_replaces_the_batch_factory_with_its_own()
    {
        using var context = Context(o => o.UseSqlite("Data Source=:memory:").UseSqliteBulk());

        context.GetService<IModificationCommandBatchFactory>()
            .ShouldBeOfType<SqliteBulkModificationCommandBatchFactory>();

        context.GetService<IBulkOperationExecutor>().ShouldBeOfType<SqliteBulkExecutor>();
    }

    [Theory]
    [InlineData("3.34.1", false)]
    [InlineData("3.35.0", true)]
    [InlineData("3.53.3", true)]

    // An unreadable version is taken to be capable: every SQLite a supported EF Core ships with
    // is, and a statement the engine cannot run fails loudly rather than writing wrongly.
    [InlineData("not a version", true)]
    public void Sqlite_reads_generated_values_back_only_where_returning_exists(
        string version,
        bool supported)
        => SqliteBulkExecutor.SupportsReturning(version).ShouldBe(supported);

    private static DbContext Context(Action<DbContextOptionsBuilder> configure)
    {
        var builder = new DbContextOptionsBuilder();
        configure(builder);
        return new DbContext(builder.Options);
    }
}
