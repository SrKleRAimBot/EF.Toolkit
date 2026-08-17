using System.Buffers;
using System.Text;
using System.Text.Json;
using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;

namespace EFToolkit.Audit.Capture;

/// <summary>
///     Writes the JSON an audit entry records a change as.
/// </summary>
/// <remarks>
///     <para>
///         The shape is deliberate. <c>old</c> and <c>new</c> are sibling objects rather than a
///         per-property pair of the two, because that is the shape a containment index can answer:
///         <c>changes @&gt; '{"new":{"Status":"Shipped"}}'</c> uses a GIN index, while the same
///         question against <c>{"Status":{"old":…,"new":…}}</c> cannot. <c>changed</c> is a flat
///         array for the same reason — <c>changes -&gt; 'changed' ? 'Status'</c> is indexable.
///     </para>
///     <para>
///         Not thread-safe, and not meant to be: one is built per unit of work, used for every row
///         in it, and thrown away. The buffer and the writer are reused across rows because an
///         audited bulk operation writes one of these per row and allocating two objects each time
///         would be the dominant cost.
///     </para>
/// </remarks>
internal sealed class AuditPayloadWriter : IDisposable
{
    private static readonly JsonSerializerOptions Fallback = new(JsonSerializerDefaults.General);

    private readonly AuditOptions _options;
    private readonly ArrayBufferWriter<byte> _buffer = new(512);
    private readonly Utf8JsonWriter _writer;

    private bool _truncated;

    public AuditPayloadWriter(AuditOptions options)
    {
        _options = options;
        _writer = new Utf8JsonWriter(_buffer, new JsonWriterOptions { SkipValidation = true });
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>
    ///     Writes one row's payload, or returns <see langword="null" /> when there is nothing to
    ///     record.
    /// </summary>
    /// <param name="operation">What happened to the row.</param>
    /// <param name="projection">The columns to record and how to read them.</param>
    /// <param name="source">Where the values come from.</param>
    /// <param name="row">Which row.</param>
    /// <param name="metadata">Ambient metadata to merge into <c>meta</c>.</param>
    /// <param name="reason">The ambient reason, recorded under <c>meta.reason</c>.</param>
    /// <returns>
    ///     The payload, or <see langword="null" /> for an update whose every property compared
    ///     equal — which is not a change and should not become an entry claiming to be one.
    /// </returns>
    public string? Write(
        AuditOperation operation,
        AuditSourceProjection projection,
        IAuditCaptureSource source,
        int row,
        IReadOnlyDictionary<string, object?> metadata,
        string? reason)
    {
        var changed = operation == AuditOperation.Update && source.HasOriginalValues
            ? Changed(projection, source, row)
            : null;

        if (changed is { Count: 0 })
        {
            return null;
        }

        _buffer.Clear();
        _writer.Reset(_buffer);
        _truncated = false;

        _writer.WriteStartObject();
        _writer.WriteString("op", Name(operation));

        WriteKey(projection, source, row);

        switch (operation)
        {
            case AuditOperation.Insert:
                WriteValues("new", projection.Columns, projection, source, row, current: true);
                break;

            case AuditOperation.Delete:
                // A delete's "current" values are the row as it stood when it was removed, which is
                // the only image there is and the one worth keeping.
                WriteValues("old", projection.Columns, projection, source, row, current: false);
                break;

            case AuditOperation.Update when changed is not null:
                _writer.WriteStartArray("changed");
                foreach (var column in changed)
                {
                    _writer.WriteStringValue(column.Plan.Name);
                }

                _writer.WriteEndArray();

                WriteValues("old", changed, projection, source, row, current: false);
                WriteValues("new", changed, projection, source, row, current: true);
                break;

            default:
                // An update from a source with no before-image. Recording the new values as though
                // they were both halves would be a lie, so the payload says what it is instead.
                _writer.WriteBoolean("partial", true);
                WriteValues("new", projection.Columns, projection, source, row, current: true);
                break;
        }

        WriteMetadata(metadata, reason);

        if (_truncated)
        {
            _writer.WriteBoolean(AuditOptions.TruncatedMarker, true);
        }

        _writer.WriteEndObject();
        _writer.Flush();

        return Encoding.UTF8.GetString(_buffer.WrittenSpan);
    }

    private static string Name(AuditOperation operation)
        => operation switch
        {
            AuditOperation.Insert => "insert",
            AuditOperation.Update => "update",
            _ => "delete",
        };

    private static List<ProjectedColumn> Changed(
        AuditSourceProjection projection,
        IAuditCaptureSource source,
        int row)
    {
        var changed = new List<ProjectedColumn>();

        foreach (var column in projection.Columns)
        {
            var before = source.GetOriginalValue(row, column.Index);
            var after = source.GetCurrentValue(row, column.Index);

            if (!AuditValues.AreEqual(column.Plan.Property, before, after))
            {
                changed.Add(column);
            }
        }

        return changed;
    }

    private void WriteKey(AuditSourceProjection projection, IAuditCaptureSource source, int row)
    {
        if (projection.PayloadKey.Count == 0)
        {
            return;
        }

        _writer.WriteStartObject("key");

        foreach (var component in projection.PayloadKey)
        {
            _writer.WritePropertyName(component.Property.Name);
            WriteJson(AuditValues.ToProvider(component.Property, component.Read(source, row)));
        }

        _writer.WriteEndObject();
    }

    private void WriteValues(
        string name,
        IReadOnlyList<ProjectedColumn> columns,
        AuditSourceProjection projection,
        IAuditCaptureSource source,
        int row,
        bool current)
    {
        _ = projection;

        _writer.WriteStartObject(name);

        foreach (var column in columns)
        {
            _writer.WritePropertyName(column.Plan.Name);

            var value = current
                ? source.GetCurrentValue(row, column.Index)
                : source.GetOriginalValue(row, column.Index);

            WriteColumn(column.Plan, value);
        }

        _writer.WriteEndObject();
    }

    private void WriteColumn(AuditPropertyPlan column, object? modelValue)
    {
        var provider = AuditValues.ToProvider(column.Property, modelValue);

        if (!column.IsMasked)
        {
            WriteJson(provider);
            return;
        }

        // A masked null stays null. Recording "***" where there was no value would say a secret is
        // set when none is, which is the kind of wrong that survives a long time in an audit trail.
        if (provider is null && column.Redactor is null)
        {
            _writer.WriteNullValue();
            return;
        }

        WriteJson(column.Redactor is null ? _options.MaskToken : column.Redactor(provider));
    }

    private void WriteMetadata(IReadOnlyDictionary<string, object?> metadata, string? reason)
    {
        if (metadata.Count == 0 && reason is null)
        {
            return;
        }

        _writer.WriteStartObject("meta");

        if (reason is not null)
        {
            _writer.WriteString("reason", reason);
        }

        // Ordered, for the same reason the columns are: two entries describing the same change
        // through different write paths have to serialize identically.
        foreach (var pair in metadata.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            _writer.WritePropertyName(pair.Key);
            WriteJson(pair.Value);
        }

        _writer.WriteEndObject();
    }

    private void WriteJson(object? value)
    {
        switch (value)
        {
            case null:
                _writer.WriteNullValue();
                return;

            case string text:
                _writer.WriteStringValue(Truncate(text));
                return;

            case bool flag:
                _writer.WriteBooleanValue(flag);
                return;

            case int number:
                _writer.WriteNumberValue(number);
                return;

            case long number:
                _writer.WriteNumberValue(number);
                return;

            case short number:
                _writer.WriteNumberValue(number);
                return;

            case byte number:
                _writer.WriteNumberValue(number);
                return;

            case decimal number:
                // Canonicalized, because a decimal's trailing zeros are part of its representation
                // and the two capture paths do not obtain them from the same place. See
                // AuditValues.Canonical.
                _writer.WriteNumberValue(AuditValues.Canonical(number));
                return;

            case double number:
                _writer.WriteNumberValue(number);
                return;

            case float number:
                _writer.WriteNumberValue(number);
                return;

            case Guid identifier:
                _writer.WriteStringValue(identifier);
                return;

            case DateTime timestamp:
                _writer.WriteStringValue(timestamp);
                return;

            case DateTimeOffset timestamp:
                _writer.WriteStringValue(timestamp);
                return;

            case byte[] bytes:
                // Base64 rather than a JSON array of numbers: shorter, and it round-trips.
                _writer.WriteStringValue(Truncate(Convert.ToBase64String(bytes)));
                return;

            default:
                JsonSerializer.Serialize(_writer, value, value.GetType(), _options.Json ?? Fallback);
                return;
        }
    }

    private string Truncate(string value)
    {
        if (_options.MaxValueLength <= 0 || value.Length <= _options.MaxValueLength)
        {
            return value;
        }

        _truncated = true;

        return value[.._options.MaxValueLength];
    }
}
