using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace STERP.Testing;

public sealed record BindingParameter(string Name, string In, bool Required);

public sealed record BindingOperation(
    string OperationId,
    string Document,
    string Method,
    string Path,
    string Server,
    IReadOnlyList<BindingParameter> Parameters,
    IReadOnlyList<string> ResponseCodes,
    IReadOnlyList<string> Requirements)
{
    public bool IsJobsHost => Server.Contains(":7102", StringComparison.Ordinal);

    public string BaseUrl => IsJobsHost ? ParityEnvironment.JobsBaseUrl : ParityEnvironment.ApiBaseUrl;
}

/// <summary>
/// The binding Agent 06 contracts copied into tests/contracts. Every file is verified against
/// binding-manifest.json (which records the Agent 06 attempt hashes) before it is used, so an edited
/// expectation cannot pass silently.
/// </summary>
public static class BindingContracts
{
    private static readonly Lazy<IReadOnlyDictionary<string, BindingOperation>> Index = new(Load);
    private static readonly Lazy<IReadOnlyList<string>> Routes = new(LoadRoutes);

    public static IReadOnlyDictionary<string, BindingOperation> Operations => Index.Value;

    public static IReadOnlyList<string> GuiRoutes => Routes.Value;

    public static string ContractFile(string relative) => Path.Combine(ParityEnvironment.ContractsDirectory, relative);

    public static void VerifyHashes()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(ContractFile("binding-manifest.json")))!;
        foreach (var entry in manifest["files"]!.AsArray())
        {
            var file = ContractFile(entry!["file"]!.GetValue<string>());
            var expected = entry["sha256"]!.GetValue<string>();
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Binding contract {file} changed (sha256 {actual}, manifest {expected}).");
            }
        }
    }

    public static BindingOperation Get(string operationId) =>
        Operations.TryGetValue(operationId, out var op)
            ? op
            : throw new InvalidOperationException($"operationId {operationId} is not in the binding OpenAPI documents");

    private static IReadOnlyDictionary<string, BindingOperation> Load()
    {
        VerifyHashes();
        var result = new Dictionary<string, BindingOperation>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(ContractFile("openapi"), "*.openapi.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            var yaml = new YamlStream();
            using (var reader = File.OpenText(file)) yaml.Load(reader);
            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            var defaultServer = FirstServer(root) ?? "https://localhost:7101";
            var components = Child(root, "components") as YamlMappingNode;
            var sharedParameters = components is null ? null : Child(components, "parameters") as YamlMappingNode;
            foreach (var path in ((YamlMappingNode)Child(root, "paths")!).Children)
            {
                var item = (YamlMappingNode)path.Value;
                var pathServer = FirstServer(item);
                foreach (var method in new[] { "get", "post", "put", "patch", "delete" })
                {
                    if (Child(item, method) is not YamlMappingNode op) continue;
                    var parameters = new List<BindingParameter>();
                    foreach (var source in new[] { Child(item, "parameters"), Child(op, "parameters") })
                    {
                        if (source is not YamlSequenceNode list) continue;
                        foreach (var p in list.Children.OfType<YamlMappingNode>())
                        {
                            var resolved = p;
                            if (Scalar(p, "$ref") is { } reference && sharedParameters is not null)
                            {
                                resolved = (YamlMappingNode)Child(sharedParameters, reference.Split('/').Last())!;
                            }
                            parameters.Add(new BindingParameter(Scalar(resolved, "name")!, Scalar(resolved, "in")!,
                                Scalar(resolved, "required") == "true"));
                        }
                    }
                    var codes = (Child(op, "responses") as YamlMappingNode)?.Children.Keys
                        .Select(k => ((YamlScalarNode)k).Value!).ToList() ?? [];
                    var reqs = (Child(op, "x-sterp-requirements") as YamlSequenceNode)?.Children
                        .Select(c => ((YamlScalarNode)c).Value!).ToList() ?? [];
                    var id = Scalar(op, "operationId")!;
                    result[id] = new BindingOperation(id, Path.GetFileName(file), method.ToUpperInvariant(),
                        ((YamlScalarNode)path.Key).Value!, FirstServer(op) ?? pathServer ?? defaultServer, parameters, codes, reqs);
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<string> LoadRoutes()
    {
        VerifyHashes();
        var doc = JsonNode.Parse(File.ReadAllText(ContractFile("gui-routes.json")))!;
        return doc["routes"]!.AsArray().Select(r => r!["route"]!.GetValue<string>()).ToList();
    }

    private static string? FirstServer(YamlMappingNode node) =>
        Child(node, "servers") is YamlSequenceNode servers && servers.Children.FirstOrDefault() is YamlMappingNode first
            ? Scalar(first, "url")
            : null;

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Scalar(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
