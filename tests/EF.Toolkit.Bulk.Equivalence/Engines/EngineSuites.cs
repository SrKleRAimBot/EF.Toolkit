using EFToolkit.Bulk.Equivalence.Infrastructure;

namespace EFToolkit.Bulk.Equivalence.Engines;

// Each engine gets its own collection so the suites can run in parallel with one container each,
// and its own Engine trait so CI can shard them across jobs.

[CollectionDefinition("postgres16")]
public sealed class PostgreSql16Collection : ICollectionFixture<PostgreSql16Fixture>;

[CollectionDefinition("postgres17")]
public sealed class PostgreSql17Collection : ICollectionFixture<PostgreSql17Fixture>;

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;

[CollectionDefinition("mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>;

[CollectionDefinition("sqlite")]
public sealed class SqliteCollection : ICollectionFixture<SqliteFixture>;

[Trait("Engine", "postgres16")]
[Collection("postgres16")]
public sealed class PostgreSql16SaveChangesTests(PostgreSql16Fixture fixture)
    : SaveChangesEquivalenceTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("postgres17")]
public sealed class PostgreSql17SaveChangesTests(PostgreSql17Fixture fixture)
    : SaveChangesEquivalenceTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("sqlserver")]
public sealed class SqlServerSaveChangesTests(SqlServerFixture fixture)
    : SaveChangesEquivalenceTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("postgres16")]
public sealed class PostgreSql16HarnessSelfTests(PostgreSql16Fixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("postgres17")]
public sealed class PostgreSql17HarnessSelfTests(PostgreSql17Fixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("sqlserver")]
public sealed class SqlServerHarnessSelfTests(SqlServerFixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("postgres16")]
public sealed class PostgreSql16PartitioningTests(PostgreSql16Fixture fixture)
    : PartitioningTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("postgres17")]
public sealed class PostgreSql17PartitioningTests(PostgreSql17Fixture fixture)
    : PartitioningTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("sqlserver")]
public sealed class SqlServerPartitioningTests(SqlServerFixture fixture)
    : PartitioningTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("postgres16")]
public sealed class PostgreSql16BulkInsertApiTests(PostgreSql16Fixture fixture)
    : BulkInsertApiTests(fixture);

// PostgreSQL 17 takes a different upsert path from 16 -- MERGE rather than ON CONFLICT -- so the
// API suite has to run there too, or the whole of that path ships unexercised.
[Trait("Engine", "postgres17")]
[Collection("postgres17")]
public sealed class PostgreSql17BulkInsertApiTests(PostgreSql17Fixture fixture)
    : BulkInsertApiTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("sqlserver")]
public sealed class SqlServerBulkInsertApiTests(SqlServerFixture fixture)
    : BulkInsertApiTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("postgres16")]
public sealed class PostgreSql16CorrectnessTests(PostgreSql16Fixture fixture)
    : CorrectnessTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("postgres17")]
public sealed class PostgreSql17CorrectnessTests(PostgreSql17Fixture fixture)
    : CorrectnessTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("sqlserver")]
public sealed class SqlServerCorrectnessTests(SqlServerFixture fixture)
    : CorrectnessTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("postgres16")]
public sealed class PostgreSql16TransactionTests(PostgreSql16Fixture fixture)
    : TransactionTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("postgres17")]
public sealed class PostgreSql17TransactionTests(PostgreSql17Fixture fixture)
    : TransactionTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("sqlserver")]
public sealed class SqlServerTransactionTests(SqlServerFixture fixture)
    : TransactionTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("postgres16")]
public sealed class PostgreSql16ThroughputTests(PostgreSql16Fixture fixture, ITestOutputHelper output)
    : ThroughputSmokeTests(fixture, output);

[Trait("Engine", "sqlserver")]
[Collection("sqlserver")]
public sealed class SqlServerThroughputTests(SqlServerFixture fixture, ITestOutputHelper output)
    : ThroughputSmokeTests(fixture, output);

[Trait("Engine", "postgres16")]
[Collection("postgres16")]
public sealed class PostgreSql16ProjectionAndScopeTests(PostgreSql16Fixture fixture)
    : ProjectionAndScopeTests(fixture);

// The delete arm is a separate DELETE under ON CONFLICT and a MERGE arm from 17 on, so a scope has
// to be exercised on both.
[Trait("Engine", "postgres17")]
[Collection("postgres17")]
public sealed class PostgreSql17ProjectionAndScopeTests(PostgreSql17Fixture fixture)
    : ProjectionAndScopeTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("sqlserver")]
public sealed class SqlServerProjectionAndScopeTests(SqlServerFixture fixture)
    : ProjectionAndScopeTests(fixture);

[Trait("Engine", "mysql")]
[Collection("mysql")]
public sealed class MySqlSaveChangesTests(MySqlFixture fixture)
    : SaveChangesEquivalenceTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("sqlite")]
public sealed class SqliteSaveChangesTests(SqliteFixture fixture)
    : SaveChangesEquivalenceTests(fixture);

[Trait("Engine", "mysql")]
[Collection("mysql")]
public sealed class MySqlHarnessSelfTests(MySqlFixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("sqlite")]
public sealed class SqliteHarnessSelfTests(SqliteFixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "mysql")]
[Collection("mysql")]
public sealed class MySqlPartitioningTests(MySqlFixture fixture)
    : PartitioningTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("sqlite")]
public sealed class SqlitePartitioningTests(SqliteFixture fixture)
    : PartitioningTests(fixture);

[Trait("Engine", "mysql")]
[Collection("mysql")]
public sealed class MySqlBulkInsertApiTests(MySqlFixture fixture)
    : BulkInsertApiTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("sqlite")]
public sealed class SqliteBulkInsertApiTests(SqliteFixture fixture)
    : BulkInsertApiTests(fixture);

[Trait("Engine", "mysql")]
[Collection("mysql")]
public sealed class MySqlCorrectnessTests(MySqlFixture fixture)
    : CorrectnessTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("sqlite")]
public sealed class SqliteCorrectnessTests(SqliteFixture fixture)
    : CorrectnessTests(fixture);

[Trait("Engine", "mysql")]
[Collection("mysql")]
public sealed class MySqlTransactionTests(MySqlFixture fixture)
    : TransactionTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("sqlite")]
public sealed class SqliteTransactionTests(SqliteFixture fixture)
    : TransactionTests(fixture);

[Trait("Engine", "mysql")]
[Collection("mysql")]
public sealed class MySqlThroughputTests(MySqlFixture fixture, ITestOutputHelper output)
    : ThroughputSmokeTests(fixture, output);

[Trait("Engine", "sqlite")]
[Collection("sqlite")]
public sealed class SqliteThroughputTests(SqliteFixture fixture, ITestOutputHelper output)
    : ThroughputSmokeTests(fixture, output);

[Trait("Engine", "mysql")]
[Collection("mysql")]
public sealed class MySqlProjectionAndScopeTests(MySqlFixture fixture)
    : ProjectionAndScopeTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("sqlite")]
public sealed class SqliteProjectionAndScopeTests(SqliteFixture fixture)
    : ProjectionAndScopeTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("postgres16")]
public sealed class PostgreSql16AccelerationTests(PostgreSql16Fixture fixture)
    : AccelerationTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("postgres17")]
public sealed class PostgreSql17AccelerationTests(PostgreSql17Fixture fixture)
    : AccelerationTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("sqlserver")]
public sealed class SqlServerAccelerationTests(SqlServerFixture fixture)
    : AccelerationTests(fixture);

[Trait("Engine", "mysql")]
[Collection("mysql")]
public sealed class MySqlAccelerationTests(MySqlFixture fixture)
    : AccelerationTests(fixture);

[Trait("Engine", "sqlite")]
[Collection("sqlite")]
public sealed class SqliteAccelerationTests(SqliteFixture fixture)
    : AccelerationTests(fixture);

// MySQL as it ships, under the lock mode that makes generated-key inserts decline. The differential
// suites run here too, because a decline that happened after the first row was written would only
// show up as divergent data.
[CollectionDefinition("mysql-interleaved")]
public sealed class MySqlInterleavedCollection : ICollectionFixture<MySqlInterleavedFixture>;

[Trait("Engine", "mysql")]
[Collection("mysql-interleaved")]
public sealed class MySqlInterleavedAutoIncrementTests(MySqlInterleavedFixture fixture)
    : InterleavedAutoIncrementTests(fixture);

[Trait("Engine", "mysql")]
[Collection("mysql-interleaved")]
public sealed class MySqlInterleavedSaveChangesTests(MySqlInterleavedFixture fixture)
    : SaveChangesEquivalenceTests(fixture);

[Trait("Engine", "mysql")]
[Collection("mysql-interleaved")]
public sealed class MySqlInterleavedBulkInsertApiTests(MySqlInterleavedFixture fixture)
    : BulkInsertApiTests(fixture);
