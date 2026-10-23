using System.Text.Json.Nodes;

namespace STERP.Testing;

public sealed record ParityScenario(
    string Id,
    string RequirementId,
    string ApplicationId,
    int? AcceptanceCriterion,
    string Title,
    string Layer,
    string Runner,
    string Dataset,
    string Confidence,
    JsonObject Context,
    JsonArray Steps,
    JsonArray Assertions,
    IReadOnlyList<string> Covers,
    string Derivation,
    string SourceFile)
{
    public override string ToString() => Id;
}

/// <summary>Loads the authored parity scenarios (tests/cases/*.cases*.json, schema sterp-parity-cases/1).</summary>
public static class CaseCatalog
{
    private static readonly Lazy<IReadOnlyList<ParityScenario>> All = new(Load);

    public static IReadOnlyList<ParityScenario> Scenarios => All.Value;

    public static ParityScenario Get(string id) =>
        Scenarios.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException($"Unknown scenario {id}");

    public static IEnumerable<string> Ids(string runner) =>
        Scenarios.Where(s => s.Runner == runner).Select(s => s.Id);

    private static IReadOnlyList<ParityScenario> Load()
    {
        var result = new List<ParityScenario>();
        if (!Directory.Exists(ParityEnvironment.CasesDirectory)) return result;
        foreach (var file in Directory.GetFiles(ParityEnvironment.CasesDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var doc = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            if (doc["schema"]?.GetValue<string>() != "sterp-parity-cases/1") continue;
            var app = doc["target_application_id"]!.GetValue<string>();
            foreach (var req in doc["requirements"]!.AsArray())
            {
                var rid = req!["requirement_id"]!.GetValue<string>();
                foreach (var node in req["scenarios"]!.AsArray())
                {
                    var s = node!.AsObject();
                    result.Add(new ParityScenario(
                        s["id"]!.GetValue<string>(),
                        rid,
                        app,
                        s["ac"]?.GetValue<int>(),
                        s["title"]?.GetValue<string>() ?? "",
                        s["layer"]!.GetValue<string>(),
                        s["runner"]!.GetValue<string>(),
                        s["dataset"]!.GetValue<string>(),
                        s["confidence"]?.GetValue<string>() ?? "unknown",
                        s["context"] as JsonObject ?? [],
                        s["steps"]!.AsArray(),
                        s["assertions"]!.AsArray(),
                        (s["covers"] as JsonArray)?.Select(c => c!.GetValue<string>()).ToList() ?? [],
                        s["derivation"]?.GetValue<string>() ?? "",
                        Path.GetFileName(file)));
                }
            }
        }
        return result;
    }
}
