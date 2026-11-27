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

    [Theory]
    [InlineData("CASCADE", false)]
    [InlineData("CASCADE", true)]
    [InlineData("NO ACTION", false)]
    [InlineData("NO ACTION", true)]
    public async Task A_table_outside_the_model_that_references_a_reset_one_stops_the_reset(string onDelete, bool inTransaction)
    {
        // The model cannot see this table, and SQLite's reset defers or suspends foreign keys: with
        // CASCADE its rows went with the customers, with NO ACTION they were left dangling.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var customer = await context.Customers.Select(c => c.Id).FirstAsync(Ct);
        var ddl = "CREATE TABLE \"Unmapped\" (\"Id\" INTEGER PRIMARY KEY, \"CustomerId\" INTEGER NOT NULL "
            + "REFERENCES \"Customers\" (\"Id\") ON DELETE " + onDelete + ")";
        await context.Database.ExecuteSqlRawAsync(ddl, Ct);
        await context.Database.ExecuteSqlAsync($"INSERT INTO \"Unmapped\" VALUES (1, {customer})", Ct);

        await using var transaction = inTransaction ? await context.Database.BeginTransactionAsync(Ct) : null;
        var error = await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct));

        error.Message.ShouldContain("'Unmapped', which the reset does not empty, references it");
        (await ScalarAsync(context, "SELECT COUNT(*) FROM \"Unmapped\"")).ShouldBe(1);
        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v > 0);
        (await ScalarAsync(context, "PRAGMA foreign_keys;")).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reset_that_fails_part_way_leaves_every_table_as_it_was(bool inTransaction)
    {
        // A trigger rejects the delete of one table after others in the cycle are already emptied.
        // Without a unit to roll back, the outer transaction committed the orphans the deferred
        // checks were switched back off for.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER \"KeepCustomers\" BEFORE DELETE ON \"Customers\" BEGIN SELECT RAISE(ABORT, 'kept'); END", Ct);

        await using (var transaction = inTransaction ? await context.Database.BeginTransactionAsync(Ct) : null)
        {
            await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(() => context.Database.ResetDataAsync(cancellationToken: Ct));
            if (transaction is not null)
            {
                await transaction.CommitAsync(Ct);
            }
        }

        (await ShopData.CountsAsync(context)).ShouldBe(before);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM pragma_foreign_key_check")).ShouldBe(0);
        (await ScalarAsync(context, "PRAGMA foreign_keys;")).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_row_a_trigger_writes_mid_reset_that_breaks_a_foreign_key_rolls_the_reset_back(bool inTransaction)
    {
        // Neither turning foreign_keys back on nor switching defer_foreign_keys off re-checks what was
        // written, so this orphan category used to be committed with the rest of the reset.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER \"LeaveOrphan\" AFTER DELETE ON \"Categories\" WHEN old.\"ParentId\" IS NULL "
                + "BEGIN INSERT INTO \"Categories\" (\"Name\", \"ParentId\") VALUES ('orphan', 2147483647); END",
            Ct);

        await using (var transaction = inTransaction ? await context.Database.BeginTransactionAsync(Ct) : null)
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("violate a foreign key");
            if (transaction is not null)
            {
                await transaction.CommitAsync(Ct);
            }
        }

        (await ShopData.CountsAsync(context)).ShouldBe(before);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM pragma_foreign_key_check")).ShouldBe(0);
    }

    [Fact]
    public async Task A_trigger_writing_an_orphan_into_a_table_outside_the_reset_rolls_it_back()
    {
        // An audit trigger on a reset table writes to an unmapped table whose foreign key points at a
        // row that does not exist. Only a whole-database check sees it.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE \"Actors\" (\"Id\" INTEGER PRIMARY KEY)", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Audit\" (\"Id\" INTEGER PRIMARY KEY, \"ActorId\" INTEGER NOT NULL REFERENCES \"Actors\" (\"Id\"))", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER \"AuditTags\" AFTER DELETE ON \"Tags\" BEGIN INSERT INTO \"Audit\" (\"ActorId\") VALUES (42); END", Ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
            .Message.ShouldContain("violate a foreign key");

        (await ShopData.CountsAsync(context)).ShouldBe(before);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM \"Audit\"")).ShouldBe(0);
    }

    [Fact]
    public async Task A_table_named_like_the_foreign_key_pragma_cannot_hide_a_new_orphan()
    {
        // SQLite read the user's empty pragma_foreign_key_check table in the pragma's place: no
        // violation was seen before or after, and the orphan the trigger wrote was committed.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE \"Actors\" (\"Id\" INTEGER PRIMARY KEY)", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Audit\" (\"Id\" INTEGER PRIMARY KEY, \"ActorId\" INTEGER NOT NULL REFERENCES \"Actors\" (\"Id\"))", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER \"AuditTags\" AFTER DELETE ON \"Tags\" BEGIN INSERT INTO \"Audit\" (\"ActorId\") VALUES (42); END", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE pragma_foreign_key_check (\"table\" TEXT, rowid INTEGER, parent TEXT, fkid INTEGER)", Ct);

        await Should.ThrowAsync<Exception>(() => context.Database.ResetDataAsync(cancellationToken: Ct));

        (await ShopData.CountsAsync(context)).ShouldBe(before);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM \"Audit\"")).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_orphan_is_caught_beside_an_old_one(bool withoutRowId)
    {
        // A WITHOUT ROWID table reports no rowid for its violations, so a snapshot comparison by rowid
        // took the new orphan for the old one.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE \"Actors\" (\"Id\" INTEGER PRIMARY KEY)", Ct);
        var audit = "CREATE TABLE \"Audit\" (\"Id\" INTEGER PRIMARY KEY, \"ActorId\" INTEGER NOT NULL REFERENCES \"Actors\" (\"Id\"))"
            + (withoutRowId ? " WITHOUT ROWID" : "");
        await context.Database.ExecuteSqlRawAsync(audit, Ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO \"Audit\" VALUES (1, 42)", Ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER \"AuditTags\" AFTER DELETE ON \"Tags\" BEGIN INSERT OR IGNORE INTO \"Audit\" VALUES (2, 43); END", Ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
            .Message.ShouldContain("violate a foreign key");

        (await ShopData.CountsAsync(context)).ShouldBe(before);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM \"Audit\"")).ShouldBe(1);
    }

    [Fact]
    public async Task A_violation_that_was_already_there_does_not_stop_the_reset()
    {
        // Rows written while foreign keys were off are not the reset's doing, and not its to refuse over.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE \"Actors\" (\"Id\" INTEGER PRIMARY KEY)", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Audit\" (\"Id\" INTEGER PRIMARY KEY, \"ActorId\" INTEGER NOT NULL REFERENCES \"Actors\" (\"Id\"))", Ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO \"Audit\" (\"ActorId\") VALUES (42)", Ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", Ct);

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM \"Audit\"")).ShouldBe(1);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM \"temp\".\"sqlite_master\" WHERE \"name\" LIKE 'ef_toolkit_fk_before%'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_view_under_a_mapped_table_s_name_is_refused_rather_than_running_its_trigger()
    {
        // An INSTEAD OF DELETE trigger on the view would run in the table's place, and could do anything.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Tags\" RENAME TO \"TagsBase\"", Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE VIEW \"Tags\" AS SELECT * FROM \"TagsBase\"", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER \"EmptyBase\" INSTEAD OF DELETE ON \"Tags\" BEGIN DELETE FROM \"TagsBase\"; END", Ct);
        var tags = await ScalarAsync(context, "SELECT COUNT(*) FROM \"TagsBase\"");

        (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
            .Message.ShouldContain("in the database it is a view");

        (await ScalarAsync(context, "SELECT COUNT(*) FROM \"TagsBase\"")).ShouldBe(tags);
        (await context.Customers.CountAsync(Ct)).ShouldBeGreaterThan(0);
    }

    private sealed class Widget
    {
        public int Id { get; set; }
    }

    private sealed class WidgetContext(DbContextOptions<WidgetContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Widget>().ToTable("widgets");
    }

    [Fact]
    public async Task A_counter_restarts_when_the_model_names_its_table_in_another_case()
    {
        // SQLite found Widgets under the model's widgets for the DELETE, but the sequence row was compared
        // exactly, so the counter kept going and the next id was 2.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE \"Widgets\" (\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT); INSERT INTO \"Widgets\" DEFAULT VALUES;";
            await create.ExecuteNonQueryAsync(Ct);
        }

        await using var context = new WidgetContext(new DbContextOptionsBuilder<WidgetContext>().UseSqlite(connection).Options);
        await context.Database.ResetDataAsync(cancellationToken: Ct);

        context.Add(new Widget());
        await context.SaveChangesAsync(Ct);
        (await context.Set<Widget>().SingleAsync(Ct)).Id.ShouldBe(1);
    }

    [Fact]
    public async Task A_trigger_that_keeps_a_row_fails_the_reset_and_every_table_is_rolled_back()
    {
        // RAISE(IGNORE) skips the delete without an error: the reset committed with the protected row still there.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER \"KeepTags\" BEFORE DELETE ON \"Tags\" BEGIN SELECT RAISE(IGNORE); END", Ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
            .Message.ShouldContain("The reset left rows in 'Tags'");

        (await ShopData.CountsAsync(context)).ShouldBe(before);
    }

    [Fact]
    public async Task A_temporary_table_of_the_caller_s_keeps_its_rows_whatever_it_is_called()
    {
        // The snapshot used one fixed name and dropped any table already called that before creating its own.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.OpenConnectionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE \"ef_toolkit_fk_before\" (\"Kept\" INTEGER)", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO \"temp\".\"ef_toolkit_fk_before\" VALUES (7)", Ct);

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
        (await ScalarAsync(context, "SELECT \"Kept\" FROM \"temp\".\"ef_toolkit_fk_before\"")).ShouldBe(7);
        await context.Database.CloseConnectionAsync();
    }

    [Fact]
    public async Task A_temporary_table_shadowing_a_mapped_one_is_not_what_the_reset_empties()
    {
        // Unqualified, DELETE FROM "Customers" would empty temp."Customers" and orphan its temporary
        // child, which the catalog check — reading main — never saw.
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.OpenConnectionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE \"Customers\" (\"Id\" INTEGER PRIMARY KEY)", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TEMP TABLE \"Child\" (\"Id\" INTEGER PRIMARY KEY, \"CustomerId\" INTEGER REFERENCES \"Customers\" (\"Id\") ON DELETE CASCADE)", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO temp.\"Customers\" VALUES (1)", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO temp.\"Child\" VALUES (1, 1)", Ct);

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await ScalarAsync(context, "SELECT COUNT(*) FROM main.\"Customers\"")).ShouldBe(0);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM temp.\"Customers\"")).ShouldBe(1);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM temp.\"Child\"")).ShouldBe(1);
    }

    [Fact]
    public async Task A_table_whose_name_differs_from_a_reset_one_only_beyond_ASCII_case_is_outside_the_reset()
    {
        // SQLite folds only ASCII in identifiers: "ärzte" is a different table from "Ärzte". A .NET
        // comparer that folded both took the unmapped one for the reset one and orphaned its rows.
        await using var db = await SqliteTestDatabase<UnicodeContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        context.Doctors.Add(new Doctor { Id = 1 });
        await context.SaveChangesAsync(Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"ärzte\" (\"Id\" INTEGER PRIMARY KEY, \"DoctorId\" INTEGER NOT NULL REFERENCES \"Ärzte\" (\"Id\"))", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO \"ärzte\" VALUES (1, 1)", Ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
            .Message.ShouldContain("'ärzte', which the reset does not empty");
        (await context.Doctors.CountAsync(Ct)).ShouldBe(1);
    }

    public sealed class Doctor
    {
        public int Id { get; set; }
    }

    public sealed class UnicodeContext(DbContextOptions<UnicodeContext> options) : DbContext(options)
    {
        public DbSet<Doctor> Doctors => Set<Doctor>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Doctor>(b =>
            {
                b.ToTable("Ärzte");
                b.Property(d => d.Id).ValueGeneratedNever();
            });
    }

    [Fact]
    public async Task A_table_outside_the_model_with_no_foreign_key_into_it_does_not_stop_the_reset()
    {
        await using var db = await SqliteTestDatabase<ShopContext>.CreateAsync(cancellationToken: Ct);
        await using var context = db.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE \"Standalone\" (\"Id\" INTEGER PRIMARY KEY)", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO \"Standalone\" VALUES (1)", Ct);

        context.Database.ResetData();

        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM \"Standalone\"")).ShouldBe(1);
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
