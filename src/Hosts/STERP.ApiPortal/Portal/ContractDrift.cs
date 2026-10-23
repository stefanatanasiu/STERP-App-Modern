using System.Security.Cryptography;
using System.Text;

namespace Sterp.ApiPortal.Portal;

/// <summary>One operation present on one side only ("missing_from_host" or "missing_from_console").</summary>
public sealed record DriftDifference(string Kind, string Method, string Path);

/// <summary>A host's copy of one document compared with the console's binding copy.</summary>
public sealed record HostCopy(
    string Host, string Url, bool Identical, string? Sha256, int Operations, IReadOnlyList<DriftDifference> Differences, string? Error);

/// <summary>Drift outcome of one document: "in-sync", "drift", "not-served", "unreachable" or "unknown".</summary>
public sealed record DocumentDrift(string Name, string Title, string Sha256, int Operations, string State, IReadOnlyList<HostCopy> Copies);

/// <summary>The document index one host published, or why it could not be read.</summary>
public sealed record HostIndex(string Host, string BaseUrl, bool Reachable, string? Error, IReadOnlyList<string> Documents);

public sealed record DriftReport(DateTimeOffset CheckedAt, IReadOnlyList<HostIndex> Hosts, IReadOnlyList<DocumentDrift> Documents);

/// <summary>
/// Contract drift: proves that the binding documents the console shows are still exactly what STERP.Api and STERP.Jobs
/// publish (/openapi/index.json and /openapi/{name}.yaml, anonymous; FR-STERP-PORTAL-001). Identical bytes are in sync;
/// otherwise the operations of both copies are compared so that a renamed or removed operation is named.
/// </summary>
public sealed class ContractDriftService(IHttpClientFactory clients, DocumentCatalog catalog, ILogger<ContractDriftService> logger)
{
    public const string ClientName = "sterp-drift";

    public async Task<DriftReport> CheckAsync(CancellationToken ct)
    {
        var indexes = await Task.WhenAll(
            IndexAsync("STERP.Api", catalog.ApiBaseUrl, ct),
            IndexAsync("STERP.Jobs", catalog.JobsBaseUrl, ct));
        var comparisons = catalog.Documents.Select(async d =>
        {
            var servedBy = indexes.Where(i => i.Documents.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var copies = await Task.WhenAll(servedBy.Select(i => CompareAsync(d, i, ct)));
            var state = copies.Length == 0
                ? (indexes.All(i => i.Reachable) ? "not-served" : "unknown")
                : copies.Any(c => c.Error is not null) ? "unreachable"
                : copies.All(c => c.Identical) ? "in-sync" : "drift";
            return new DocumentDrift(d.Name, d.Title, d.Sha256, d.OperationCount, state, copies);
        });
        return new DriftReport(DateTimeOffset.UtcNow, indexes, await Task.WhenAll(comparisons));
    }

    private async Task<HostIndex> IndexAsync(string host, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var http = clients.CreateClient(ClientName);
            var index = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync(baseUrl + "/openapi/index.json", ct));
            var names = (index?["documents"] as System.Text.Json.Nodes.JsonArray ?? [])
                .Select(d => d?["name"]?.GetValue<string>() ?? "")
                .Where(n => n.Length > 0)
                .ToList();
            return new HostIndex(host, baseUrl, true, null, names);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            logger.LogWarning("{Service} document index at {Url} is not reachable: {Error}", host, baseUrl, ex.Message);
            return new HostIndex(host, baseUrl, false, ex.Message, []);
        }
    }

    private async Task<HostCopy> CompareAsync(PortalDocument d, HostIndex host, CancellationToken ct)
    {
        var url = $"{host.BaseUrl}/openapi/{Uri.EscapeDataString(d.Name)}.yaml";
        try
        {
            using var http = clients.CreateClient(ClientName);
            var bytes = await http.GetByteArrayAsync(url, ct);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (sha == d.Sha256) return new HostCopy(host.Host, url, true, sha, d.OperationCount, [], null);

            var served = DocumentCatalog.OperationKeys(YamlJson.Convert(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')));
            var binding = DocumentCatalog.OperationKeys(d.Json);
            var differences = served.Except(binding).Select(k => Difference("missing_from_console", k))
                .Concat(binding.Except(served).Select(k => Difference("missing_from_host", k)))
                .OrderBy(x => x.Path, StringComparer.Ordinal)
                .ThenBy(x => x.Method, StringComparer.Ordinal)
                .ToList();
            return new HostCopy(host.Host, url, false, sha, served.Count, differences, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // one unreadable copy is reported on its own document instead of failing the whole check
            logger.LogWarning("{Document} from {Url} could not be compared: {Error}", d.Name, url, ex.Message);
            return new HostCopy(host.Host, url, false, null, 0, [], ex.Message);
        }
    }

    private static DriftDifference Difference(string kind, string key)
    {
        var space = key.IndexOf(' ', StringComparison.Ordinal);
        return new DriftDifference(kind, key[..space], key[(space + 1)..]);
    }
}
