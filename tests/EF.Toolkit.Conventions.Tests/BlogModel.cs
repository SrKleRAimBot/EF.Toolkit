using System.Data.Common;
using EFToolkit.Conventions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFToolkit.Conventions.Tests;

public sealed class Author
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Post> Posts { get; } = [];
    public AuthorProfile? Profile { get; set; }
}

/// <summary>Owned by Author, and itself the owner of a collection stored in a table of its own.</summary>
public sealed class AuthorProfile
{
    public string Bio { get; set; } = "";
    public List<ProfileLink> Links { get; } = [];
}

public sealed class ProfileLink
{
    public int Id { get; set; }
    public string Url { get; set; } = "";
}

public class Post
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int Tenant { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public int? AuthorId { get; set; }
    public Author? Author { get; set; }
    public Seo Seo { get; set; } = new();
    public List<Comment> Comments { get; } = [];
}

public sealed class FeaturedPost : Post
{
    public int Rank { get; set; }
}

/// <summary>Owned and table-split: its columns live in the Posts row.</summary>
public sealed class Seo
{
    public string Slug { get; set; } = "";
}

public sealed class Comment
{
    public int Id { get; set; }
    public int PostId { get; set; }
    public string Text { get; set; } = "";
    public DateTime? DeletedAt { get; set; }

    /// <summary>An optimistic-concurrency token, so a soft delete can lose a race.</summary>
    public int Version { get; set; }

    /// <summary>A complex property: its members are columns of the comment's own row.</summary>
    public CommentMeta Meta { get; set; } = new();

    /// <summary>An optional complex property.</summary>
    public CommentMeta? Extra { get; set; }

    /// <summary>A complex collection, stored as JSON in the comment's row.</summary>
    public List<CommentMeta> Reactions { get; set; } = [];

    /// <summary>An optional complex value with an optional complex value nested in it.</summary>
    public Place? Place { get; set; }

    /// <summary>An optional complex value whose nested complex value is required when it is present.</summary>
    public Spot? Spot { get; set; }

    /// <summary>An optional value object, with a private constructor and a private setter.</summary>
    public Badge? Badge { get; set; }
}

public sealed class Spot
{
    public int Floor { get; set; }
    public Geo Geo { get; set; } = new();
}

public sealed class Badge
{
    private Badge()
    {
    }

    public Badge(string code) => Code = code;

    public string Code { get; private set; } = "";
}

public sealed class Place
{
    /// <summary>Required: EF needs one to tell an absent optional complex value from an empty one.</summary>
    public int Floor { get; set; }

    public string? Name { get; set; }
    public Geo? Geo { get; set; }
}

public sealed class Geo
{
    public double Lat { get; set; }
    public double Lng { get; set; }
}

public sealed class CommentMeta
{
    public string Mood { get; set; } = "calm";
}

/// <summary>No soft delete, shadow timestamps.</summary>
public sealed class Note
{
    public int Id { get; set; }
    public string Body { get; set; } = "";

    /// <summary>Optional, and ClientNoAction: EF's delete fixup never nulls it.</summary>
    public int? AuthorId { get; set; }
    public Author? Author { get; set; }
}

public sealed class BlogContext(DbContextOptions<BlogContext> options, int? tenant = null) : DbContext(options)
{
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Author>(b =>
        {
            b.HasSoftDelete();
            b.OwnsOne(a => a.Profile, p => p.OwnsMany(x => x.Links, l => l.Property(x => x.Id).ValueGeneratedNever()));
        });

        modelBuilder.Entity<Post>(b =>
        {
            b.HasSoftDelete(p => p.IsDeleted);
            b.HasCreatedAt(p => p.CreatedAt);
            b.HasUpdatedAt(p => p.UpdatedAt);
            b.HasQueryFilter("Tenant", p => tenant == null || p.Tenant == tenant);
            b.OwnsOne(p => p.Seo);
            b.HasIndex(p => p.Title).IsUnique();
            b.HasOne(p => p.Author).WithMany(a => a.Posts).HasForeignKey(p => p.AuthorId).IsRequired(false);

            // ClientCascade: EF deletes loaded comments with their post, and the database has no cascade
            // of its own — a hard-deleted post's DELETE fails if any comment row is left behind.
            b.HasMany(p => p.Comments).WithOne().HasForeignKey(c => c.PostId).OnDelete(DeleteBehavior.ClientCascade);
        });

        modelBuilder.Entity<FeaturedPost>();
        modelBuilder.Entity<Comment>(b =>
        {
            b.HasSoftDelete(c => c.DeletedAt);
            b.Property(c => c.Version).IsConcurrencyToken();
            b.ComplexProperty(c => c.Meta);
            b.ComplexProperty(c => c.Extra);
            b.ComplexCollection(c => c.Reactions, r => r.ToJson());
            b.ComplexProperty(c => c.Place, p => p.ComplexProperty(x => x.Geo));
            b.ComplexProperty(c => c.Spot, p => p.ComplexProperty(x => x.Geo));
            b.ComplexProperty(c => c.Badge);
        });
        modelBuilder.Entity<Note>(b =>
        {
            b.HasTimestamps();
            b.HasOne(n => n.Author).WithMany().HasForeignKey(n => n.AuthorId).OnDelete(DeleteBehavior.ClientNoAction);
        });
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>One in-memory database per test, with contexts that share its open connection.</summary>
public sealed class BlogDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public FakeClock Clock { get; } = new(Start);

    public List<string> Commands { get; } = [];

    /// <summary>The shared connection, for contexts built outside <see cref="Context" />.</summary>
    public SqliteConnection Connection => _connection;

    public static async Task<BlogDatabase> CreateAsync()
    {
        var database = new BlogDatabase();
        await database._connection.OpenAsync();

        // EF turns foreign keys on only for connections it opens itself; this one is opened here and
        // shared, so without this the suite would never notice a write that breaks a constraint.
        await using (var pragma = database._connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();
        }
        await using var context = database.Context();
        await context.Database.EnsureCreatedAsync();
        return database;
    }

    public BlogContext Context(
        int? tenant = null,
        Action<ConventionsOptionsBuilder>? configure = null,
        bool conventions = true,
        params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<BlogContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new CommandRecorder(Commands))
            .AddInterceptors(interceptors)
            // Every test has a clock of its own, and the clock is part of the options EF caches
            // service providers by — so this suite creates far more of them than an application would.
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        if (conventions)
        {
            builder.UseEntityConventions(c =>
            {
                c.Clock(Clock);
                configure?.Invoke(c);
            });
        }

        return new BlogContext(builder.Options, tenant);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private sealed class CommandRecorder(List<string> commands) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            commands.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }
    }
}

/// <summary>
///     Comments alone, on the same table, in a context EF can pool: pooling needs a constructor taking
///     only the options, which BlogContext's tenant parameter rules out.
/// </summary>
public sealed class PooledCommentsContext(DbContextOptions<PooledCommentsContext> options) : DbContext(options)
{
    public DbSet<Comment> Comments => Set<Comment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Comment>(b =>
        {
            b.ToTable("Comments");
            b.HasSoftDelete(c => c.DeletedAt);
            b.Property(c => c.Version).IsConcurrencyToken();
            b.ComplexProperty(c => c.Meta);
            b.ComplexProperty(c => c.Extra);
            b.ComplexCollection(c => c.Reactions, r => r.ToJson());
            b.ComplexProperty(c => c.Place, p => p.ComplexProperty(x => x.Geo));
            b.ComplexProperty(c => c.Spot, p => p.ComplexProperty(x => x.Geo));
            b.ComplexProperty(c => c.Badge);
        });
}
