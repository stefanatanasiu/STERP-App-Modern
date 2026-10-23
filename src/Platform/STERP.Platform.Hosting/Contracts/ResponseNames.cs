using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Sterp.Platform.Hosting.Contracts;

/// <summary>
/// Gives a success body the property spelling of the operation's binding response schema (contracts 4.3). Result-set
/// columns reach JSON through the generic camelCase policy, which keeps acronyms (<c>TotalDebitGBP</c> becomes
/// <c>totalDebitGBP</c>) while many schemas spell them <c>totalDebitGbp</c>. A property is renamed only when the schema
/// does not name it exactly and exactly one schema property matches it case-insensitively; values, order and every other
/// property are left untouched.
/// </summary>
public static class ResponseNames
{
    private const int MaxDepth = 64;

    private static readonly ConcurrentDictionary<JsonObject, Shape> Shapes = new(ReferenceEqualityComparer.Instance);

    private sealed record Shape(
        IReadOnlyDictionary<string, JsonObject?> Properties,
        IReadOnlyDictionary<string, string> ByLowerName,
        JsonObject? Additional,
        JsonObject? Items);

    /// <summary>Conforms <paramref name="body"/> in place to the schema of the operation's <paramref name="status"/> response.</summary>
    public static void Conform(ContractOperation op, int status, JsonNode? body)
    {
        if (body is null) return;
        var doc = ContractCatalog.Instance.Document(op.Document)?.Json;
        if (doc is null) return;
        var schema = ResponseSchema(op.Definition, status, doc);
        if (schema is not null) Walk(body, schema, doc, 0);
    }

    /// <summary>The application/json schema of a response code (component responses resolved), or null.</summary>
    public static JsonObject? ResponseSchema(JsonObject operation, int status, JsonObject doc)
    {
        if (operation["responses"] is not JsonObject responses) return null;
        var response = responses[status.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
        for (var i = 0; i < 8 && response?["$ref"]?.GetValue<string>() is { } reference; i++)
        {
            response = Resolve(doc, reference);
        }
        return response?["content"]?["application/json"]?["schema"] as JsonObject;
    }

    private static void Walk(JsonNode node, JsonObject schema, JsonObject doc, int depth)
    {
        if (depth > MaxDepth) return;
        var shape = ShapeOf(schema, doc);
        switch (node)
        {
            case JsonObject obj:
                RenameProperties(obj, shape);
                foreach (var (name, value) in obj.ToList())
                {
                    if (value is null) continue;
                    var child = shape.Properties.TryGetValue(name, out var s) ? s : shape.Additional;
                    if (child is not null) Walk(value, child, doc, depth + 1);
                }
                break;
            case JsonArray arr when shape.Items is not null:
                foreach (var item in arr)
                {
                    if (item is not null) Walk(item, shape.Items, doc, depth + 1);
                }
                break;
        }
    }

    private static void RenameProperties(JsonObject obj, Shape shape)
    {
        if (shape.ByLowerName.Count == 0) return;
        var renames = false;
        foreach (var (name, _) in obj)
        {
            if (!shape.Properties.ContainsKey(name) && shape.ByLowerName.TryGetValue(name.ToLowerInvariant(), out var exact) && !obj.ContainsKey(exact))
            {
                renames = true;
                break;
            }
        }
        if (!renames) return;
        var pairs = obj.ToList();
        obj.Clear();
        foreach (var (name, value) in pairs)
        {
            var target = name;
            if (!shape.Properties.ContainsKey(name) && shape.ByLowerName.TryGetValue(name.ToLowerInvariant(), out var exact) && !obj.ContainsKey(exact)
                && !pairs.Any(p => string.Equals(p.Key, exact, StringComparison.Ordinal)))
            {
                target = exact;
            }
            obj[target] = value;
        }
    }

    private static Shape ShapeOf(JsonObject schema, JsonObject doc) => Shapes.GetOrAdd(schema, s =>
    {
        var properties = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        JsonObject? additional = null;
        JsonObject? items = null;
        Collect(s, doc, properties, ref additional, ref items, 0);
        var byLower = properties.Keys.GroupBy(k => k.ToLowerInvariant(), StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        return new Shape(properties, byLower, additional, items);
    });

    private static void Collect(JsonObject? schema, JsonObject doc, Dictionary<string, JsonObject?> properties,
        ref JsonObject? additional, ref JsonObject? items, int depth)
    {
        if (schema is null || depth > MaxDepth) return;
        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            Collect(Resolve(doc, reference), doc, properties, ref additional, ref items, depth + 1);
            return;
        }
        if (schema["properties"] is JsonObject props)
        {
            foreach (var (name, propertySchema) in props)
            {
                properties.TryAdd(name, propertySchema as JsonObject);
            }
        }
        if (schema["additionalProperties"] is JsonObject extra) additional ??= extra;
        if (schema["items"] is JsonObject itemSchema) items ??= itemSchema;
        foreach (var composition in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (schema[composition] is not JsonArray members) continue;
            foreach (var member in members)
            {
                Collect(member as JsonObject, doc, properties, ref additional, ref items, depth + 1);
            }
        }
    }

    private static JsonObject? Resolve(JsonObject doc, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        JsonNode? node = doc;
        foreach (var part in reference[2..].Split('/'))
        {
            node = node?[part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
        }
        return node as JsonObject;
    }
}
