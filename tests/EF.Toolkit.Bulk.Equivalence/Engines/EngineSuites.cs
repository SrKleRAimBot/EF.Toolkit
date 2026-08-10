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
