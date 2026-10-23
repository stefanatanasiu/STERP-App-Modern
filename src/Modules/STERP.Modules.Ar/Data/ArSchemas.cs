using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Sterp.Platform.Hosting.Contracts;

namespace Sterp.Modules.Ar.Data;

/// <summary>
/// Shapes result-set rows to the binding AR component schemas: property names exactly as the schema spells them (the
/// AR schemas write sterling columns as <c>...Gbp</c>, e.g. <c>BalanceGBP</c> -> <c>balanceGbp</c>, while the generic
/// camelCase of the row reader gives <c>balanceGBP</c>), in schema order, every schema property present (null when the row
/// lacks it); columns the schema does not name are kept after them.
/// </summary>
public static class ArSchemas
{
    private static readonly ConcurrentDictionary<string, string[]> Properties = new(StringComparer.Ordinal);

    public static string[] Of(string schema) => Properties.GetOrAdd(schema, name =>
    {
        var doc = ContractCatalog.Instance.Document("STERP-AR") ?? throw new InvalidOperationException("STERP-AR contract not embedded");
        var node = doc.Json["components"]?["schemas"]?[name] as JsonObject ?? throw new InvalidOperationException($"schema {name} not in STERP-AR");
        return (node["properties"] as JsonObject)?.Select(p => p.Key).ToArray() ?? [];
    });

    public static JsonObject Conform(JsonObject row, string schema)
    {
        var names = Of(schema);
        var source = row.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var o = new JsonObject();
        foreach (var n in names)
        {
            o[n] = source.Remove(n, out var v) ? v?.DeepClone() : null;
        }
        foreach (var (k, v) in source) o[k] = v?.DeepClone();
        return o;
    }

    public static JsonArray ConformRows(JsonArray rows, string schema) =>
        new(rows.Select(r => (JsonNode?)(r is JsonObject o ? Conform(o, schema) : r?.DeepClone())).ToArray());

    /// <summary>First row of a result set conformed to the schema, or null when the set is empty or missing.</summary>
    public static JsonObject? FirstOrNull(IReadOnlyList<JsonArray> sets, int index, string schema) =>
        index < sets.Count && sets[index].Count > 0 && sets[index][0] is JsonObject o ? Conform(o, schema) : null;

    /// <summary>A result set conformed to the schema (empty when missing).</summary>
    public static JsonArray Rows(IReadOnlyList<JsonArray> sets, int index, string schema) =>
        index < sets.Count ? ConformRows(sets[index], schema) : [];
}
