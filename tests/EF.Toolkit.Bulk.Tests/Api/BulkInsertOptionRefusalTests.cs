using EFToolkit.Bulk.Api;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Api;

/// <summary>
///     What an insert does with the options that belong to some other operation.
/// </summary>
/// <remarks>
///     Every one of these refusals exists for the same reason: an option that is accepted and then
///     does nothing is indistinguishable from one that worked, and the two most likely to be reached
///     for here — <c>WithinScope</c> and <c>InsertOnly</c> — are both there to hold something back.
///     A graph insert takes its own path through the API and so has to refuse them separately, which
///     is what these assert.
/// </remarks>
public class BulkInsertOptionRefusalTests
{
    [Fact]
    public async Task A_graph_insert_refuses_a_scope()
    {
        var thrown = await Refused(o => o.IncludeGraph().WithinScope(c => c.TenantId == 1));

        thrown.Message.ShouldContain("WithinScope applies to BulkSynchronizeAsync");

        // The kind reads as prose rather than as a lower-cased enum.
        thrown.Message.ShouldContain("an insert");
    }

    [Fact]
    public async Task A_graph_insert_refuses_a_sql_scope()
    {
        // The escape hatch has to refuse on the same terms as the expression overload; it is the one
        // more likely to carry a tenant filter someone is relying on.
        var thrown = await Refused(o => o.IncludeGraph().WithinScope($"t.tenant_id = {1}"));

        thrown.Message.ShouldContain("WithinScope applies to BulkSynchronizeAsync");
    }

    [Fact]
    public async Task A_graph_insert_refuses_InsertOnly()
    {
        var thrown = await Refused(o => o.IncludeGraph().InsertOnly(c => c.CreatedAt));

        thrown.Message.ShouldContain("InsertOnly has no meaning");
    }

    [Fact]
    public async Task A_graph_insert_refuses_MatchOn()
    {
        var thrown = await Refused(o => o.IncludeGraph().MatchOn(c => c.Email));

        thrown.Message.ShouldContain("MatchOn has no meaning");
    }

    [Fact]
    public async Task A_graph_insert_refuses_a_projection()
    {
        var thrown = await Refused(o => o.IncludeGraph().Include(c => c.Name));

        thrown.Message.ShouldContain("cannot be combined with IncludeGraph()");
    }

    [Fact]
    public async Task A_plain_insert_refuses_MatchOn_too()
    {
        // Not a graph concern: an insert locates nothing whichever path it takes, so the graph guard
        // being stricter than the insert beside it would be its own kind of surprise.
        var thrown = await Refused(o => o.MatchOn(c => c.Email));

        thrown.Message.ShouldContain("MatchOn has no meaning");
    }

    private static async Task<BulkNotSupportedException> Refused(
        Action<BulkOperationOptionsBuilder<Customer>> configure)
    {
        await using var context = new ShopContext();

        // Refused before anything is opened, so the unreachable connection string is never used.
        return await Should.ThrowAsync<BulkNotSupportedException>(
            () => context.BulkInsertAsync([new Customer()], configure, TestContext.Current.CancellationToken));
    }

    private sealed class Customer
    {
        public int Id { get; set; }
        public int TenantId { get; set; }
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public List<Order> Orders { get; } = [];
    }

    private sealed class Order
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public decimal Total { get; set; }
    }

    private sealed class ShopContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseSqlServer("Server=none;Database=none")
                .UseSqlServerBulk();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>();
            modelBuilder.Entity<Order>();
        }
    }
}
