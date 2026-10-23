using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Sterp.Platform.Hosting.Contracts;

public sealed record ContractParameter(string Name, string In, bool Required, JsonObject? Schema);

/// <summary>One binding operation of an Agent 06 OpenAPI document (contracts 4.11).</summary>
public sealed record ContractOperation(
    string OperationId,
    string Document,
    string Slice,
    string Wave,
    string Method,
    string Path,
    bool IsJobsHost,
    string Outcome,
    string Engine,
    IReadOnlyList<string> ScreenGate,
    IReadOnlyList<ContractParameter> Parameters,
    IReadOnlyList<string> ResponseCodes,
    IReadOnlyList<string> Requirements,
    JsonObject Definition)
{
    public bool IsEnvelope => string.Equals(Outcome, "envelope", StringComparison.Ordinal);

    public ContractParameter? Header(string name) =>
        Parameters.FirstOrDefault(p => p.In == "header" && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool HasHeader(string name) => Header(name) is not null;

    public bool RequiresHeader(string name) => Header(name)?.Required == true;
}

/// <summary>A binding document: exact bytes (served as YAML), its JSON form and metadata.</summary>
public sealed record ContractDocument(string Name, string FileName, string Title, string Slice, string Wave, byte[] YamlBytes, JsonObject Json)
{
    public string ShortSlice => Slice.StartsWith("STERP-", StringComparison.Ordinal) ? Slice[6..] : Slice;
}

/// <summary>
/// Loads the binding Agent 06 OpenAPI documents embedded in this assembly and exposes every operation's metadata. The
/// router registers a route for every operation, so the served surface always equals the binding contract.
/// </summary>
public sealed class ContractCatalog
{
    /// <summary>Slice -> wave (contracts section 3); host documents belong to W0.</summary>
    public static readonly IReadOnlyDictionary<string, string> Waves = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["STERP-API"] = "W0", ["STERP-JOBS"] = "W0", ["STERP-PLATFORM"] = "W0", ["STERP-ITM"] = "W1", ["STERP-AR"] = "W2",
        ["STERP-SO"] = "W3", ["STERP-XMD"] = "W4", ["STERP-FIN"] = "W5", ["STERP-PO"] = "W6", ["STERP-AP"] = "W7",
        ["STERP-BOM"] = "W8", ["STERP-KIT"] = "W9", ["STERP-CST"] = "W10",
    };

    private static readonly Lazy<ContractCatalog> Shared = new(() => Load(typeof(ContractCatalog).Assembly));

    public static ContractCatalog Instance => Shared.Value;

    private ContractCatalog(IReadOnlyList<ContractDocument> documents, IReadOnlyList<ContractOperation> operations)
    {
        Documents = documents;
        Operations = operations;
        ById = operations.ToDictionary(o => o.OperationId, StringComparer.Ordinal);
    }

    public IReadOnlyList<ContractDocument> Documents { get; }

    public IReadOnlyList<ContractOperation> Operations { get; }

    public IReadOnlyDictionary<string, ContractOperation> ById { get; }

    public ContractOperation Get(string operationId) =>
        ById.TryGetValue(operationId, out var op) ? op : throw new InvalidOperationException($"operationId {operationId} is not in the binding OpenAPI documents");

    public ContractDocument? Document(string name) =>
        Documents.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    public static ContractCatalog Load(Assembly assembly)
    {
        var documents = new List<ContractDocument>();
        var operations = new List<ContractOperation>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("openapi/", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var fileName = resource["openapi/".Length..];
            var name = fileName.Replace(".openapi.yaml", "", StringComparison.Ordinal);
            var json = YamlJson.Convert(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            var title = json["info"]?["title"]?.GetValue<string>() ?? name;
            var wave = Waves.TryGetValue(name, out var w) ? w : "";
            documents.Add(new ContractDocument(name, fileName, title, name, wave, bytes, json));
            operations.AddRange(ReadOperations(name, wave, json));
        }
        return new ContractCatalog(documents, operations);
    }

    private static IEnumerable<ContractOperation> ReadOperations(string document, string wave, JsonObject doc)
    {
        var defaultServer = FirstServer(doc) ?? "https://localhost:7101";
        var sharedParameters = doc["components"]?["parameters"] as JsonObject;
        foreach (var (path, itemNode) in (JsonObject)doc["paths"]!)
        {
            var item = (JsonObject)itemNode!;
            var pathServer = FirstServer(item);
            foreach (var method in new[] { "get", "post", "put", "patch", "delete" })
            {
                if (item[method] is not JsonObject op) continue;
                var parameters = new List<ContractParameter>();
                foreach (var source in new[] { item["parameters"], op["parameters"] })
                {
                    if (source is not JsonArray list) continue;
                    foreach (var pNode in list)
                    {
                        var p = (JsonObject)pNode!;
                        if (p["$ref"]?.GetValue<string>() is { } reference && sharedParameters is not null)
                        {
                            p = (JsonObject)sharedParameters[reference.Split('/').Last()]!;
                        }
                        parameters.Add(new ContractParameter(
                            p["name"]!.GetValue<string>(), p["in"]!.GetValue<string>(),
                            p["required"]?.GetValue<bool>() == true, p["schema"] as JsonObject));
                    }
                }
                var server = FirstServer(op) ?? pathServer ?? defaultServer;
                var gate = (op["x-sterp-screen-gate"]?["anyOf"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [];
                var codes = (op["responses"] as JsonObject)?.Select(r => r.Key).ToList() ?? [];
                var reqs = (op["x-sterp-requirements"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [];
                yield return new ContractOperation(
                    op["operationId"]!.GetValue<string>(), document, document, op["x-sterp-wave"]?.GetValue<string>() ?? wave,
                    method.ToUpperInvariant(), path, server.Contains(":7102", StringComparison.Ordinal),
                    op["x-sterp-outcome"]?.GetValue<string>() ?? "direct", op["x-sterp-engine"]?.GetValue<string>() ?? "online",
                    gate, parameters, codes, reqs, op);
            }
        }
    }

    private static string? FirstServer(JsonObject node) =>
        node["servers"] is JsonArray servers && servers.Count > 0 ? servers[0]?["url"]?.GetValue<string>() : null;
}

/// <summary>YAML (YamlDotNet representation model) to JSON conversion of the binding documents.</summary>
public static class YamlJson
{
    public static JsonObject Convert(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (JsonObject)ToNode(stream.Documents[0].RootNode)!;
    }

    private static JsonNode? ToNode(YamlNode node) => node switch
    {
        YamlMappingNode map => ToObject(map),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToNode).ToArray()),
        YamlScalarNode scalar => Scalar(scalar),
        _ => null,
    };

    private static JsonObject ToObject(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in map.Children)
        {
            obj[((YamlScalarNode)k).Value ?? ""] = ToNode(v);
        }
        return obj;
    }

    private static JsonNode? Scalar(YamlScalarNode s)
    {
        var v = s.Value ?? "";
        if (s.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
        {
            return JsonValue.Create(v);
        }
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }
        if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }
}
