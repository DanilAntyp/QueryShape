using System.Text;
using System.Text.Json;

namespace QueryShape.Testing;

/// <summary>Deterministic JSON for snapshot files: fixed key order, 2-space indent, <c>\n</c> line endings, trailing newline.</summary>
public static class SnapshotSerializer
{
    /// <summary>Serializes a snapshot.</summary>
    public static string Serialize(QuerySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", snapshot.Version);
            writer.WriteNumber("queryCount", snapshot.QueryCount);

            writer.WriteStartArray("queries");
            foreach (var q in snapshot.Queries)
            {
                writer.WriteStartObject();
                writer.WriteString("fingerprint", q.Fingerprint);
                writer.WriteString("shape", q.Shape);
                writer.WriteString("source", q.Source);
                writer.WriteStartArray("tags");
                foreach (var t in q.Tags)
                {
                    writer.WriteStringValue(t);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("diagnostics");
            foreach (var d in snapshot.Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("ruleId", d.RuleId);
                writer.WriteString("severity", d.Severity);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
        return json.EndsWith('\n') ? json : json + "\n";
    }

    /// <summary>Parses a snapshot file. Throws <see cref="JsonException"/> on malformed content.</summary>
    public static QuerySnapshot Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = root.TryGetProperty("version", out var v) ? v.GetInt32() : 1;
        var queries = new List<SnapshotQuery>();
        if (root.TryGetProperty("queries", out var qs))
        {
            foreach (var q in qs.EnumerateArray())
            {
                var tags = new List<string>();
                if (q.TryGetProperty("tags", out var ts))
                {
                    foreach (var t in ts.EnumerateArray())
                    {
                        tags.Add(t.GetString() ?? string.Empty);
                    }
                }

                queries.Add(new SnapshotQuery(
                    q.GetProperty("fingerprint").GetString() ?? string.Empty,
                    q.TryGetProperty("shape", out var sh) ? sh.GetString() ?? string.Empty : string.Empty,
                    q.TryGetProperty("source", out var so) ? so.GetString() ?? "Linq" : "Linq",
                    tags));
            }
        }

        var diagnostics = new List<SnapshotDiagnostic>();
        if (root.TryGetProperty("diagnostics", out var ds))
        {
            foreach (var d in ds.EnumerateArray())
            {
                diagnostics.Add(new SnapshotDiagnostic(
                    d.GetProperty("ruleId").GetString() ?? string.Empty,
                    d.TryGetProperty("severity", out var sev) ? sev.GetString() ?? "Info" : "Info"));
            }
        }

        return new QuerySnapshot
        {
            Version = version,
            QueryCount = root.TryGetProperty("queryCount", out var qc) ? qc.GetInt32() : queries.Count,
            Queries = queries,
            Diagnostics = diagnostics,
        };
    }
}
