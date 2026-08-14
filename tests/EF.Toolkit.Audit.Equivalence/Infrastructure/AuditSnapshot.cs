using System.Text.Json;
using EFToolkit.Audit.Configuration;

namespace EFToolkit.Audit.Equivalence.Infrastructure;

/// <summary>
///     The audit table, read over ADO.
/// </summary>
/// <remarks>
///     Deliberately not read through EF. The payload column's whole point is that the database
///     stores it as JSON rather than as text that happens to look like JSON, and a read that goes
///     back through the same mapping that wrote it would be satisfied either way.
/// </remarks>
public static class AuditSnapshot
{
    /// <summary>Reads every audit entry, ordered so two runs can be compared.</summary>
    /// <param name="fixture">The database to read.</param>
    public static async Task<List<AuditRow>> ReadAsync(AuditDatabaseFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var table = $"{fixture.Quote(AuditOptions.DefaultSchema)}."
            + $"{fixture.Quote(AuditOptions.DefaultTableName)}";

        var columns = string.Join(
            ", ",
            new[] { "EntityType", "EntityKey", "Operation", "ActorId", "ActorName", "ActorType",
                    "TenantId", "Source", "Changes", "OccurredAt", "CorrelationId" }
                .Select(fixture.Quote));

        await using var connection = fixture.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {columns} FROM {table}";

        var rows = new List<AuditRow>();

        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(new AuditRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7),
                    Canonical(reader.GetString(8)),

                    // Read as an offset rather than a DateTime: SQL Server's datetimeoffset comes
                    // back as one and refuses the narrowing, while Npgsql converts timestamptz to
                    // either. The offset is also what the column actually holds.
                    await reader.GetFieldValueAsync<DateTimeOffset>(9),
                    reader.IsDBNull(10) ? null : reader.GetGuid(10)));
            }
        }

        // Ordered by content rather than by key, because the key is generated and the two runs
        // being compared have no reason to produce the same one.
        rows.Sort(static (a, b) => string.CompareOrdinal(a.SortKey, b.SortKey));

        return rows;
    }

    /// <summary>
    ///     Reparses and rewrites the payload so two engines' JSON is textually comparable.
    /// </summary>
    /// <remarks>
    ///     PostgreSQL's <c>jsonb</c> is a parsed representation, so what comes back out is
    ///     normalised — whitespace gone, keys reordered, numbers reformatted — while SQL Server
    ///     returns the exact text that went in. Round-tripping both through the same writer compares
    ///     what the payload <em>says</em> rather than how one engine chose to store it.
    /// </remarks>
    private static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Write(document.RootElement);
    }

    private static string Write(JsonElement element)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteOrdered(writer, element);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteOrdered(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteOrdered(writer, property.Value);
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteOrdered(writer, item);
                }

                writer.WriteEndArray();
                return;

            default:
                element.WriteTo(writer);
                return;
        }
    }
}

/// <summary>One audit entry, as stored.</summary>
/// <param name="EntityType">The audited type.</param>
/// <param name="EntityKey">The audited row's key, rendered.</param>
/// <param name="Operation">1 insert, 2 update, 3 delete.</param>
/// <param name="ActorId">Who acted.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="ActorType">What kind of principal they are.</param>
/// <param name="TenantId">The tenant the row belongs to.</param>
/// <param name="Source">Which write path produced the entry.</param>
/// <param name="Changes">The payload, canonicalised.</param>
/// <param name="OccurredAt">When.</param>
/// <param name="CorrelationId">What ties it to the rest of its unit of work.</param>
public sealed record AuditRow(
    string EntityType,
    string EntityKey,
    int Operation,
    string? ActorId,
    string? ActorName,
    string? ActorType,
    string? TenantId,
    string Source,
    string Changes,
    DateTimeOffset OccurredAt,
    Guid? CorrelationId)
{
    /// <summary>Everything two runs of the same change must agree on, as one comparable string.</summary>
    /// <remarks>
    ///     The entry's own key, its timestamp, its correlation id and its source are all left out.
    ///     The first three differ between two runs by construction; the fourth differs by design,
    ///     since saying which path produced an entry is the one thing the two paths must
    ///     <em>not</em> agree on.
    /// </remarks>
    public string SortKey
        => $"{EntityType}|{EntityKey}|{Operation}|{ActorId}|{ActorName}|{ActorType}|{TenantId}|{Changes}";
}
