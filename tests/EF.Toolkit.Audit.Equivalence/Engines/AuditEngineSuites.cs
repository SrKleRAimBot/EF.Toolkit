using EFToolkit.Audit.Equivalence.Infrastructure;

namespace EFToolkit.Audit.Equivalence.Engines;

// One collection per engine, so each gets its own container and the engines run in parallel. The
// Engine trait is what lets CI shard the suite across jobs.

/// <summary>Fixture collection for PostgreSQL 16.</summary>
[CollectionDefinition("audit-postgres16")]
public sealed class PostgreSql16Collection : ICollectionFixture<PostgreSql16AuditFixture>;

/// <summary>Fixture collection for PostgreSQL 17.</summary>
[CollectionDefinition("audit-postgres17")]
public sealed class PostgreSql17Collection : ICollectionFixture<PostgreSql17AuditFixture>;

/// <summary>Fixture collection for SQL Server 2022.</summary>
[CollectionDefinition("audit-sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerAuditFixture>;

[Trait("Engine", "postgres16")]
[Collection("audit-postgres16")]
public sealed class PostgreSql16CaptureTests(PostgreSql16AuditFixture fixture)
    : AuditCaptureTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("audit-postgres17")]
public sealed class PostgreSql17CaptureTests(PostgreSql17AuditFixture fixture)
    : AuditCaptureTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("audit-sqlserver")]
public sealed class SqlServerCaptureTests(SqlServerAuditFixture fixture)
    : AuditCaptureTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("audit-postgres16")]
public sealed class PostgreSql16BulkAuditTests(PostgreSql16AuditFixture fixture)
    : BulkAuditTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("audit-postgres17")]
public sealed class PostgreSql17BulkAuditTests(PostgreSql17AuditFixture fixture)
    : BulkAuditTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("audit-sqlserver")]
public sealed class SqlServerBulkAuditTests(SqlServerAuditFixture fixture)
    : BulkAuditTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("audit-postgres16")]
public sealed class PostgreSql16EquivalenceTests(PostgreSql16AuditFixture fixture)
    : AuditEquivalenceTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("audit-postgres17")]
public sealed class PostgreSql17EquivalenceTests(PostgreSql17AuditFixture fixture)
    : AuditEquivalenceTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("audit-sqlserver")]
public sealed class SqlServerEquivalenceTests(SqlServerAuditFixture fixture)
    : AuditEquivalenceTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("audit-postgres16")]
public sealed class PostgreSql16AtomicityTests(PostgreSql16AuditFixture fixture)
    : AuditAtomicityTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("audit-postgres17")]
public sealed class PostgreSql17AtomicityTests(PostgreSql17AuditFixture fixture)
    : AuditAtomicityTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("audit-sqlserver")]
public sealed class SqlServerAtomicityTests(SqlServerAuditFixture fixture)
    : AuditAtomicityTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("audit-postgres16")]
public sealed class PostgreSql16HarnessSelfTests(PostgreSql16AuditFixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("audit-postgres17")]
public sealed class PostgreSql17HarnessSelfTests(PostgreSql17AuditFixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "sqlserver")]
[Collection("audit-sqlserver")]
public sealed class SqlServerHarnessSelfTests(SqlServerAuditFixture fixture)
    : HarnessSelfTests(fixture);

[Trait("Engine", "postgres16")]
[Collection("audit-postgres16")]
public sealed class PostgreSql16PayloadQueryTests(PostgreSql16AuditFixture fixture)
    : PayloadQueryTests(fixture);

[Trait("Engine", "postgres17")]
[Collection("audit-postgres17")]
public sealed class PostgreSql17PayloadQueryTests(PostgreSql17AuditFixture fixture)
    : PayloadQueryTests(fixture);
