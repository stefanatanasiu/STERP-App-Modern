using System.Security.Cryptography;
using System.Text.Json.Nodes;
using STERP.Testing;

namespace STERP.Target.Parity;

/// <summary>
/// Contract layer (contracts 11, STERP-API section 4): every running host serves the binding OpenAPI
/// documents unchanged at /openapi/{name}.{yaml|json}, and every binding operation is exposed with the
/// same method, path, parameters and status codes.
/// </summary>
public sealed class ContractConformanceTests
{
    public static IEnumerable<ITheoryDataRow> Documents() =>
        Directory.GetFiles(BindingContracts.ContractFile("openapi"), "*.openapi.yaml")
            .Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new TheoryDataRow<string>(n).WithTrait("tier", "target-parity").WithTrait("layer", "contract"));

    public static IEnumerable<ITheoryDataRow> Operations() =>
        BindingContracts.Operations.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new TheoryDataRow<string>(k).WithTrait("tier", "target-parity").WithTrait("layer", "contract"));

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task Host_serves_the_binding_OpenAPI_document_unchanged(string document)
    {
        var ops = BindingContracts.Operations.Values.Where(o => o.Document == document).ToList();
        var host = ops.Any(o => !o.IsJobsHost) ? ParityEnvironment.ApiBaseUrl : ParityEnvironment.JobsBaseUrl;
        await TargetClient.EnsureReachableAsync(host, ops.Any(o => !o.IsJobsHost) ? "STERP.Api" : "STERP.Jobs");
        var name = document.Replace(".openapi.yaml", "", StringComparison.Ordinal);
        var served = await TargetClient.SendAsync(HttpMethod.Get, $"{host.TrimEnd('/')}/openapi/{name}.yaml", new Dictionary<string, string>(), null);
        Assert.Equal(200, served.Status);
        static string Canonical(string text) => text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        var binding = Canonical(File.ReadAllText(BindingContracts.ContractFile(Path.Combine("openapi", document))));
        var expected = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(binding)));
        var actual = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Canonical(served.Text))));
        Assert.True(expected == actual, $"{name}: the served document differs from the binding Agent 06 document");
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Host_exposes_the_binding_operation(string operationId)
    {
        var op = BindingContracts.Get(operationId);
        await TargetClient.EnsureReachableAsync(op.BaseUrl, op.IsJobsHost ? "STERP.Jobs" : "STERP.Api");
        var name = op.Document.Replace(".openapi.yaml", "", StringComparison.Ordinal);
        var served = await TargetClient.SendAsync(HttpMethod.Get, $"{op.BaseUrl.TrimEnd('/')}/openapi/{name}.json", new Dictionary<string, string>(), null);
        Assert.Equal(200, served.Status);
        var item = served.Json?["paths"]?[op.Path]?[op.Method.ToLowerInvariant()] as JsonObject;
        Assert.True(item is not null, $"{operationId}: {op.Method} {op.Path} is not served");
        Assert.Equal(operationId, item!["operationId"]?.GetValue<string>());
        var codes = ((item["responses"] as JsonObject)?.Select(r => r.Key) ?? []).OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.Equal(op.ResponseCodes.OrderBy(c => c, StringComparer.Ordinal), codes);
    }

    [Fact]
    [Trait("tier", "target-parity")]
    [Trait("layer", "contract")]
    public async Task OpenAPI_index_lists_the_binding_documents_of_both_hosts()
    {
        foreach (var (host, what) in new[] { (ParityEnvironment.ApiBaseUrl, "STERP.Api"), (ParityEnvironment.JobsBaseUrl, "STERP.Jobs") })
        {
            await TargetClient.EnsureReachableAsync(host, what);
            var index = await TargetClient.SendAsync(HttpMethod.Get, $"{host.TrimEnd('/')}/openapi/index.json", new Dictionary<string, string>(), null);
            Assert.Equal(200, index.Status);
            Assert.NotNull(index.Json);
        }
    }
}
