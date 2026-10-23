using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Sterp.ApiPortal.Portal;

/// <summary>One tag of a document, in document order.</summary>
public sealed record PortalTag(string Name, string Description);

/// <summary>One operation of a binding document, as the API Console lists it.</summary>
public sealed record PortalOperation(
    string Document, string OperationId, string Method, string Path, string Summary,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Requirements, bool OnJobsHost);

/// <summary>One binding OpenAPI document (contracts/openapi, embedded byte-identical).</summary>
public sealed record PortalDocument(
    string Name, string FileName, string Title, string Version, string Wave, string WebService,
    IReadOnlyList<PortalTag> Tags, IReadOnlyList<PortalOperation> Operations, byte[] YamlBytes, string Sha256, JsonObject Json)
{
    /// <summary>Slice documents carry the slice id as their name; the two host documents describe a web service.</summary>
    public bool IsHostDocument => Name is "STERP-API" or "STERP-JOBS";

    public int OperationCount => Operations.Count;

    /// <summary>The title without the product prefix and the document name, e.g. "Sales order processing".</summary>
    public string ShortTitle
    {
        get
        {
            var t = Title;
            if (t.StartsWith("STERP ", StringComparison.Ordinal)) t = t[6..];
            var suffix = $" ({Name})";
            if (t.EndsWith(suffix, StringComparison.Ordinal)) t = t[..^suffix.Length];
            if (t.EndsWith(" API", StringComparison.Ordinal)) t = t[..^4];
            return t;
        }
    }
}

/// <summary>
/// The thirteen binding documents (FR-STERP-PORTAL-001/002): read from the embedded copies only - the console holds no
/// business data and never connects to the database. The console copy points the servers at the configured hosts.
/// </summary>
public sealed class DocumentCatalog
{
    /// <summary>Binding local server URLs of the documents (contracts 4.11).</summary>
    public const string BindingApiUrl = "https://localhost:7101";
    public const string BindingJobsUrl = "https://localhost:7102";

    /// <summary>Document -> wave (contracts section 3); the host documents belong to W0.</summary>
    public static readonly IReadOnlyDictionary<string, string> Waves = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["STERP-API"] = "W0", ["STERP-JOBS"] = "W0", ["STERP-PLATFORM"] = "W0", ["STERP-ITM"] = "W1", ["STERP-AR"] = "W2",
        ["STERP-SO"] = "W3", ["STERP-XMD"] = "W4", ["STERP-FIN"] = "W5", ["STERP-PO"] = "W6", ["STERP-AP"] = "W7",
        ["STERP-BOM"] = "W8", ["STERP-KIT"] = "W9", ["STERP-CST"] = "W10",
    };

    /// <summary>HTTP methods of an OpenAPI path item.</summary>
    public static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    private readonly Dictionary<string, byte[]> _consoleJson = new(StringComparer.OrdinalIgnoreCase);

    public DocumentCatalog(string apiBaseUrl, string jobsBaseUrl)
    {
        ApiBaseUrl = apiBaseUrl.TrimEnd('/');
        JobsBaseUrl = jobsBaseUrl.TrimEnd('/');
        Documents = Load(typeof(DocumentCatalog).Assembly)
            .OrderBy(d => d.IsHostDocument ? 0 : 1)
            .ThenBy(d => int.Parse(d.Wave.TrimStart('W'), System.Globalization.CultureInfo.InvariantCulture))
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var d in Documents)
        {
            _consoleJson[d.Name] = Encoding.UTF8.GetBytes(WithConfiguredServers(d.Json).ToJsonString());
        }
    }

    public string ApiBaseUrl { get; }

    public string JobsBaseUrl { get; }

    public IReadOnlyList<PortalDocument> Documents { get; }

    public int OperationCount => Documents.Sum(d => d.OperationCount);

    public PortalDocument? Find(string name) =>
        Documents.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The JSON the console renders: the binding document with its servers pointing at the configured hosts.</summary>
    public byte[] ConsoleJson(string name) => _consoleJson[name];

    /// <summary>"GET /api/so/v1/sales-orders" keys of the operations a parsed document declares.</summary>
    public static HashSet<string> OperationKeys(JsonObject doc)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, item) in doc["paths"] as JsonObject ?? [])
        {
            if (item is not JsonObject pathItem) continue;
            foreach (var m in Methods)
            {
                if (pathItem[m] is JsonObject) keys.Add($"{m.ToUpperInvariant()} {path}");
            }
        }
        return keys;
    }

    private static List<PortalDocument> Load(Assembly assembly)
    {
        var documents = new List<PortalDocument>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("openapi/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var fileName = resource["openapi/".Length..];
            var name = fileName.Replace(".openapi.yaml", "", StringComparison.Ordinal);
            var json = YamlJson.Convert(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            var info = json["info"] as JsonObject;
            documents.Add(new PortalDocument(
                name, fileName,
                info?["title"]?.GetValue<string>() ?? name,
                info?["version"]?.ToString() ?? "",
                Waves.TryGetValue(name, out var w) ? w : "",
                WebServices(json),
                Tags(json),
                Operations(name, json),
                bytes,
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                json));
        }
        return documents;
    }

    /// <summary>Declared tags in document order, then any tag an operation uses without declaring it.</summary>
    private static List<PortalTag> Tags(JsonObject doc)
    {
        var tags = new List<PortalTag>();
        foreach (var t in doc["tags"] as JsonArray ?? [])
        {
            if (t?["name"]?.GetValue<string>() is { Length: > 0 } name)
            {
                tags.Add(new PortalTag(name, t["description"]?.GetValue<string>() ?? ""));
            }
        }
        foreach (var op in Operations("", doc))
        {
            foreach (var tag in op.Tags.Where(tag => tags.All(t => t.Name != tag))) tags.Add(new PortalTag(tag, ""));
        }
        return tags;
    }

    private static List<PortalOperation> Operations(string name, JsonObject doc)
    {
        var operations = new List<PortalOperation>();
        var rootServer = FirstServer(doc["servers"]) ?? BindingApiUrl;
        foreach (var (path, item) in doc["paths"] as JsonObject ?? [])
        {
            if (item is not JsonObject pathItem) continue;
            var pathServer = FirstServer(pathItem["servers"]);
            foreach (var (method, node) in pathItem)
            {
                if (node is not JsonObject op || !Methods.Contains(method)) continue;
                var tags = Strings(op["tags"]);
                var server = FirstServer(op["servers"]) ?? pathServer ?? rootServer;
                operations.Add(new PortalOperation(
                    name,
                    op["operationId"]?.GetValue<string>() ?? $"{method}{path}",
                    method.ToUpperInvariant(),
                    path,
                    op["summary"]?.GetValue<string>() ?? "",
                    tags.Count > 0 ? tags : ["default"],
                    Strings(op["x-sterp-requirements"]),
                    string.Equals(server, BindingJobsUrl, StringComparison.OrdinalIgnoreCase)));
            }
        }
        return operations;
    }

    private static List<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList();

    private static string? FirstServer(JsonNode? servers) =>
        (servers as JsonArray ?? []).Select(s => (s?["url"]?.GetValue<string>() ?? "").TrimEnd('/')).FirstOrDefault(u => u.Length > 0);

    /// <summary>The web services a document's operations run on (root servers and the path-level job servers).</summary>
    private static string WebServices(JsonObject doc)
    {
        var urls = new HashSet<string>(Servers(doc), StringComparer.OrdinalIgnoreCase);
        var hasApi = urls.Contains(BindingApiUrl);
        var hasJobs = urls.Contains(BindingJobsUrl);
        return hasApi && hasJobs ? "STERP.Api, STERP.Jobs" : hasJobs ? "STERP.Jobs" : "STERP.Api";
    }

    private static IEnumerable<string> Servers(JsonObject doc)
    {
        IEnumerable<string> Urls(JsonNode? servers) =>
            (servers as JsonArray ?? []).Select(s => (s?["url"]?.GetValue<string>() ?? "").TrimEnd('/')).Where(u => u.Length > 0);

        foreach (var u in Urls(doc["servers"])) yield return u;
        foreach (var (_, item) in doc["paths"] as JsonObject ?? [])
        {
            if (item is not JsonObject path) continue;
            foreach (var u in Urls(path["servers"])) yield return u;
            foreach (var m in Methods)
            {
                if (path[m] is JsonObject op) foreach (var u in Urls(op["servers"])) yield return u;
            }
        }
    }

    private JsonObject WithConfiguredServers(JsonObject binding)
    {
        var doc = (JsonObject)binding.DeepClone();
        Rewrite(doc["servers"]);
        foreach (var (_, item) in doc["paths"] as JsonObject ?? [])
        {
            if (item is not JsonObject path) continue;
            Rewrite(path["servers"]);
            foreach (var m in Methods)
            {
                if (path[m] is JsonObject op) Rewrite(op["servers"]);
            }
        }
        return doc;
    }

    private void Rewrite(JsonNode? servers)
    {
        foreach (var s in servers as JsonArray ?? [])
        {
            if (s is not JsonObject server) continue;
            var url = (server["url"]?.GetValue<string>() ?? "").TrimEnd('/');
            if (string.Equals(url, BindingApiUrl, StringComparison.OrdinalIgnoreCase)) server["url"] = ApiBaseUrl;
            else if (string.Equals(url, BindingJobsUrl, StringComparison.OrdinalIgnoreCase)) server["url"] = JobsBaseUrl;
        }
    }
}
