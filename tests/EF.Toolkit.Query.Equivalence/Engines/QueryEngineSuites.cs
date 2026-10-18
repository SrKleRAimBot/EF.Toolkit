using EFToolkit.Query.Equivalence.Infrastructure;

namespace EFToolkit.Query.Equivalence.Engines;

// One collection per engine, so each gets its own container and the engines run in parallel. The
// Engine trait is what lets CI shard the suite across jobs.

[CollectionDefinition("query-postgres16")]
public sealed class PostgreSql16Collection : ICollectionFixture<PostgreSql16QueryFixture>;

[CollectionDefinition("query-postgres17")]
public sealed class PostgreSql17Collection : ICollectionFixture<PostgreSql17QueryFixture>;

[CollectionDefinition("query-sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerQueryFixture>;

[CollectionDefinition("query-mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlQueryFixture>;

[CollectionDefinition("query-sqlite")]
public sealed class SqliteCollection : ICollectionFixture<SqliteQueryFixture>;

[Trait("Engine", "postgres16")]
[Collection("query-postgres16")]
public sealed class PostgreSql16KeysetPagingTests(PostgreSql16QueryFixture fixture)
    : KeysetPagingTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("query-postgres17")]
public sealed class PostgreSql17KeysetPagingTests(PostgreSql17QueryFixture fixture)
    : KeysetPagingTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("query-sqlserver")]
public sealed class SqlServerKeysetPagingTests(SqlServerQueryFixture fixture)
    : KeysetPagingTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("query-postgres16")]
public sealed class PostgreSql16OffsetPagingTests(PostgreSql16QueryFixture fixture)
    : OffsetPagingTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("query-postgres17")]
public sealed class PostgreSql17OffsetPagingTests(PostgreSql17QueryFixture fixture)
    : OffsetPagingTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("query-sqlserver")]
public sealed class SqlServerOffsetPagingTests(SqlServerQueryFixture fixture)
    : OffsetPagingTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("query-postgres16")]
public sealed class PostgreSql16SortingAndFilteringTests(PostgreSql16QueryFixture fixture)
    : SortingAndFilteringTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("query-postgres17")]
public sealed class PostgreSql17SortingAndFilteringTests(PostgreSql17QueryFixture fixture)
    : SortingAndFilteringTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("query-sqlserver")]
public sealed class SqlServerSortingAndFilteringTests(SqlServerQueryFixture fixture)
    : SortingAndFilteringTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("query-postgres16")]
public sealed class PostgreSql16StreamingTests(PostgreSql16QueryFixture fixture)
    : StreamingTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("query-postgres17")]
public sealed class PostgreSql17StreamingTests(PostgreSql17QueryFixture fixture)
    : StreamingTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("query-sqlserver")]
public sealed class SqlServerStreamingTests(SqlServerQueryFixture fixture)
    : StreamingTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("query-postgres16")]
public sealed class PostgreSql16TrackingScopeTests(PostgreSql16QueryFixture fixture)
    : TrackingScopeTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("query-postgres17")]
public sealed class PostgreSql17TrackingScopeTests(PostgreSql17QueryFixture fixture)
    : TrackingScopeTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("query-sqlserver")]
public sealed class SqlServerTrackingScopeTests(SqlServerQueryFixture fixture)
    : TrackingScopeTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("query-postgres16")]
public sealed class PostgreSql16HarnessSelfTests(PostgreSql16QueryFixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("query-postgres17")]
public sealed class PostgreSql17HarnessSelfTests(PostgreSql17QueryFixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("query-sqlserver")]
public sealed class SqlServerHarnessSelfTests(SqlServerQueryFixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "mysql")]
[Collection("query-mysql")]
public sealed class MySqlKeysetPagingTests(MySqlQueryFixture fixture)
    : KeysetPagingTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("query-sqlite")]
public sealed class SqliteKeysetPagingTests(SqliteQueryFixture fixture)
    : KeysetPagingTests(fixture);

[Trait("Engine", "mysql")]
[Collection("query-mysql")]
public sealed class MySqlOffsetPagingTests(MySqlQueryFixture fixture)
    : OffsetPagingTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("query-sqlite")]
public sealed class SqliteOffsetPagingTests(SqliteQueryFixture fixture)
    : OffsetPagingTests(fixture);

[Trait("Engine", "mysql")]
[Collection("query-mysql")]
public sealed class MySqlSortingAndFilteringTests(MySqlQueryFixture fixture)
    : SortingAndFilteringTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("query-sqlite")]
public sealed class SqliteSortingAndFilteringTests(SqliteQueryFixture fixture)
    : SortingAndFilteringTests(fixture);

[Trait("Engine", "mysql")]
[Collection("query-mysql")]
public sealed class MySqlStreamingTests(MySqlQueryFixture fixture)
    : StreamingTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("query-sqlite")]
public sealed class SqliteStreamingTests(SqliteQueryFixture fixture)
    : StreamingTests(fixture);

[Trait("Engine", "mysql")]
[Collection("query-mysql")]
public sealed class MySqlTrackingScopeTests(MySqlQueryFixture fixture)
    : TrackingScopeTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("query-sqlite")]
public sealed class SqliteTrackingScopeTests(SqliteQueryFixture fixture)
    : TrackingScopeTests(fixture);

[Trait("Engine", "mysql")]
[Collection("query-mysql")]
public sealed class MySqlHarnessSelfTests(MySqlQueryFixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("query-sqlite")]
public sealed class SqliteHarnessSelfTests(SqliteQueryFixture fixture)
    : HarnessSelfTests(fixture);
