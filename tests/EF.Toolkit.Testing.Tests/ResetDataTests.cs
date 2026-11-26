using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Testing.Tests;

public sealed class ResetDataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> ScalarAsync(ShopContext context, string sql)
    {
        await context.Database.OpenConnectionAsync(Ct);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Every_table_is_emptied_across_chains_self_references_and_cycles()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);

        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v > 0);
        (await context.Categories.CountAsync(c => c.ParentId != null, Ct)).ShouldBe(2);
        (await context.Teams.CountAsync(t => t.CaptainId != null, Ct)).ShouldBe(1);

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
    }

    [Fact]
    public async Task Identity_restarts_so_the_next_row_gets_id_1()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await context.SeedAsync(ShopData.Customer(), ShopData.Customer(), ShopData.Customer());

        await context.Database.ResetDataAsync(cancellationToken: Ct);
        var customer = await context.SeedOneAsync(ShopData.Customer(), Ct);

        customer.Id.ShouldBe(1);
    }

    [Fact]
    public async Task Identity_continues_when_asked()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await context.SeedAsync(ShopData.Customer(), ShopData.Customer());

        await context.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct);
        var customer = await context.SeedOneAsync(ShopData.Customer(), Ct);

        customer.Id.ShouldBe(3);
    }

    [Fact]
    public async Task Excluded_tables_keep_their_rows()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);

        await context.Database.ResetDataAsync(o => o.ExcludeEntity<Product>().ExcludeTable("Tags"), Ct);

        var counts = await ShopData.CountsAsync(context);
        counts["Products"].ShouldBe(2);
        counts["Tags"].ShouldBe(1);
        counts["OrderLines"].ShouldBe(0);
        counts["Customers"].ShouldBe(0);
    }

    [Fact]
    public async Task Foreign_keys_are_back_on_afterwards()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await ScalarAsync(context, "PRAGMA foreign_keys;")).ShouldBe(1);
        context.Add(new Player { Name = "Orphan", TeamId = 999 });
        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Foreign_keys_stay_off_when_they_were_off()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;", Ct);

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await ScalarAsync(context, "PRAGMA foreign_keys;")).ShouldBe(0);
    }

    [Fact]
    public async Task The_change_tracker_is_cleared()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        context.Add(ShopData.Customer());
        await context.SaveChangesAsync(Ct);
        context.ChangeTracker.Entries().ShouldNotBeEmpty();

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_database_without_autoincrement_tables_has_no_sequence_table_to_clear()
    {
        await using var db = await SqliteTestDatabase<TagsContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await context.SeedOneAsync(new Tag { Code = "x", Label = "x" }, Ct);

        await context.Database.ResetDataAsync(cancellationToken: Ct);
        context.Database.ResetData();

        (await context.Tags.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_reset_inside_a_transaction_keeps_the_callers_deferred_foreign_keys()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var scope = await db.BeginIsolatedScopeAsync(cancellationToken: Ct);
        var context = scope.CreateContext();

        await context.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;", Ct);
        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await ScalarAsync(context, "PRAGMA defer_foreign_keys;")).ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_exclusion_deletes_nothing()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);

        await Should.ThrowAsync<InvalidOperationException>(
            () => context.Database.ResetDataAsync(o => o.ExcludeEntity<OrderLine>(), Ct));

        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v > 0);
    }

    [Fact]
    public async Task A_reset_inside_a_transaction_is_rolled_back_with_it()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using (var seed = db.CreateContext())
        {
            await ShopData.SeedEverythingAsync(seed);
        }

        await using (var scope = await db.BeginIsolatedScopeAsync(cancellationToken: Ct))
        {
            var context = scope.CreateContext();
            await context.Database.ResetDataAsync(cancellationToken: Ct);
            (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);

            // Foreign keys are immediate again for the rest of the transaction.
            (await ScalarAsync(context, "PRAGMA defer_foreign_keys;")).ShouldBe(0);
            context.Add(new Player { Name = "Orphan", TeamId = 999 });
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        }

        await using var after = db.CreateContext();
        (await ShopData.CountsAsync(after)).Values.ShouldAllBe(v => v > 0);
    }

    [Fact]
    public async Task The_synchronous_reset_does_the_same()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        context.Add(ShopData.Customer());

        context.Database.ResetData();

        context.ChangeTracker.Entries().ShouldBeEmpty();
        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
        (await context.SeedOneAsync(ShopData.Customer(), Ct)).Id.ShouldBe(1);
        (await ScalarAsync(context, "PRAGMA foreign_keys;")).ShouldBe(1);
    }

    [Fact]
    public async Task The_synchronous_reset_restores_foreign_keys_even_when_a_delete_fails()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();

        // A trigger that refuses deletes, standing in for any failure halfway through the body.
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER \"NoDelete\" BEFORE DELETE ON \"Tags\" BEGIN SELECT RAISE(ABORT, 'refused'); END;", Ct);
        await context.SeedOneAsync(new Tag { Code = "x", Label = "x" }, Ct);

        Should.Throw<Exception>(() => context.Database.ResetData()).Message.ShouldContain("refused");
        (await ScalarAsync(context, "PRAGMA foreign_keys;")).ShouldBe(1);

        await Should.ThrowAsync<Exception>(() => context.Database.ResetDataAsync(cancellationToken: Ct));
        (await ScalarAsync(context, "PRAGMA foreign_keys;")).ShouldBe(1);
    }

    [Fact]
    public async Task A_model_with_no_tables_resets_without_touching_the_database()
    {
        await using var context = new EmptyContext(
            new DbContextOptionsBuilder<EmptyContext>().UseSqlite("Data Source=/nonexistent/dir/never.db").Options);

        await Should.NotThrowAsync(() => context.Database.ResetDataAsync(cancellationToken: Ct));
        Should.NotThrow(() => context.Database.ResetData());
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Should.Throw<ArgumentNullException>(() => DatabaseResetExtensions.ResetData(null!));
        Should.Throw<ArgumentNullException>(() => DatabaseResetExtensions.ResetDataAsync(null!));
    }

    public sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options);

    public sealed class TagsContext(DbContextOptions<TagsContext> options) : DbContext(options)
    {
        public DbSet<Tag> Tags => Set<Tag>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Tag>().HasKey(t => t.Code);
    }
}
