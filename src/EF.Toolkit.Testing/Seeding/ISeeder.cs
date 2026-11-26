using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing;

/// <summary>Reference data a test database is created with.</summary>
/// <typeparam name="TContext">The context type.</typeparam>
/// <remarks>
///     A class rather than a lambda when the same data serves several fixtures. Register it with
///     <see cref="TestDatabaseBuilder{TContext}.Seed(ISeeder{TContext})" />.
/// </remarks>
public interface ISeeder<in TContext>
    where TContext : DbContext
{
    /// <summary>Adds the data. Anything added and not yet saved is saved afterwards.</summary>
    /// <param name="context">A fresh context, used for this seeder alone.</param>
    /// <param name="cancellationToken">Cancels the seeding.</param>
    /// <returns>A task that completes once the data is added.</returns>
    Task SeedAsync(TContext context, CancellationToken cancellationToken);
}
