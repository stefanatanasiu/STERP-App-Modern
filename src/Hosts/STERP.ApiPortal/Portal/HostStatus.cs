using System.Text.Json.Nodes;

namespace Sterp.ApiPortal.Portal;

/// <summary>One API host as the console last observed it.</summary>
/// <param name="Id">"api" or "jobs".</param>
/// <param name="Readiness">Status of /health/ready (Healthy, Degraded, Unhealthy) or Unreachable.</param>
/// <param name="IndexRead">True when /openapi/index.json answered, so its document list is authoritative.</param>
public sealed record HostState(
    string Id, string Name, string BaseUrl, int Port, bool Live, string Readiness, string Detail, bool IndexRead,
    IReadOnlyList<string> Documents);

/// <summary>
/// Liveness, readiness and served documents of STERP.Api and STERP.Jobs. A document is delivered when a host serves it,
/// planned when the hosts that would serve it answer without it, and unknown while such a host is not answering.
/// </summary>
public sealed record HostsStatus(IReadOnlyList<HostState> Hosts, IReadOnlySet<string> Delivered, IReadOnlyList<string> Problems, DateTimeOffset CheckedAt)
{
    public bool IsDelivered(string name) => Delivered.Contains(name);

    public HostState Host(string id) => Hosts.First(h => h.Id == id);

    /// <summary>"delivered", "planned" or "unknown".</summary>
    public string StateOf(string name)
    {
        if (Delivered.Contains(name)) return "delivered";
        var relevant = name switch
        {
            "STERP-API" => Hosts.Where(h => h.Id == "api"),
            "STERP-JOBS" => Hosts.Where(h => h.Id == "jobs"),
            _ => Hosts,
        };
        return relevant.All(h => h.IndexRead) ? "planned" : "unknown";
    }

    public JsonObject ToJson(DocumentCatalog catalog)
    {
        var states = new JsonObject();
        foreach (var d in catalog.Documents) states[d.Name] = StateOf(d.Name);
        return new JsonObject
        {
            ["checkedAt"] = CheckedAt.ToString("O"),
            ["hosts"] = new JsonArray(Hosts.Select(h => (JsonNode)new JsonObject
            {
                ["id"] = h.Id,
                ["name"] = h.Name,
                ["baseUrl"] = h.BaseUrl,
                ["port"] = h.Port,
                ["live"] = h.Live,
                ["readiness"] = h.Readiness,
                ["detail"] = h.Detail,
                ["indexRead"] = h.IndexRead,
                ["documents"] = new JsonArray(h.Documents.Select(n => (JsonNode)JsonValue.Create(n)).ToArray()),
            }).ToArray()),
            ["documents"] = states,
            ["problems"] = new JsonArray(Problems.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()),
        };
    }
}

/// <summary>
/// Probes the two API hosts for the status bar, the catalogue states and the console's own readiness. Only anonymous
/// endpoints are read (/health/live, /health/ready, /openapi/index.json). Answers are cached briefly; an unreachable host
/// is reported, never guessed.
/// </summary>
public sealed class HostStatusService(IHttpClientFactory clients, DocumentCatalog catalog, ILogger<HostStatusService> logger)
{
    public const string ClientName = "sterp-hosts";

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HostsStatus? _last;

    public async Task<HostsStatus> GetAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && Fresh() is { } cached) return cached;
        await _gate.WaitAsync(ct);
        try
        {
            if (!refresh && Fresh() is { } again) return again;
            // the probes are not tied to the page request, so a closed browser tab never caches a false "not reachable"
            var probes = await Task.WhenAll(
                ProbeAsync("api", "STERP.Api", catalog.ApiBaseUrl),
                ProbeAsync("jobs", "STERP.Jobs", catalog.JobsBaseUrl)).WaitAsync(ct);
            var delivered = new HashSet<string>(probes.SelectMany(p => p.State.Documents), StringComparer.OrdinalIgnoreCase);
            _last = new HostsStatus(
                probes.Select(p => p.State).ToList(),
                delivered,
                probes.SelectMany(p => p.Problems).ToList(),
                DateTimeOffset.UtcNow);
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    private HostsStatus? Fresh() =>
        _last is { } last && DateTimeOffset.UtcNow - last.CheckedAt < CacheFor ? last : null;

    private async Task<(HostState State, List<string> Problems)> ProbeAsync(string id, string name, string baseUrl)
    {
        var port = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Port : 0;
        var problems = new List<string>();
        using var http = clients.CreateClient(ClientName);
        try
        {
            using var live = await http.GetAsync(baseUrl + "/health/live");
            if (!live.IsSuccessStatusCode)
            {
                var detail = $"/health/live answered {(int)live.StatusCode}.";
                problems.Add($"{name} ({baseUrl}) is not live: {detail}");
                return (new HostState(id, name, baseUrl, port, false, "Unhealthy", detail, false, []), problems);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("{Service} at {Url} is not reachable: {Error}", name, baseUrl, ex.Message);
            problems.Add($"{name} ({baseUrl}) is not reachable: {ex.Message}");
            return (new HostState(id, name, baseUrl, port, false, "Unreachable", $"No answer from {baseUrl}. {ex.Message}", false, []), problems);
        }

        var (readiness, readyDetail) = await ReadinessAsync(http, baseUrl);
        var documents = new List<string>();
        var indexRead = false;
        try
        {
            using var response = await http.GetAsync(baseUrl + "/openapi/index.json");
            response.EnsureSuccessStatusCode();
            var index = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            foreach (var d in index?["documents"] as JsonArray ?? [])
            {
                if (d?["name"]?.GetValue<string>() is { Length: > 0 } doc) documents.Add(doc);
            }
            indexRead = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            logger.LogWarning("{Service} document index at {Url} is not reachable: {Error}", name, baseUrl, ex.Message);
            problems.Add($"{name} ({baseUrl}) does not list its documents: {ex.Message}");
        }
        return (new HostState(id, name, baseUrl, port, true, readiness, readyDetail, indexRead, documents), problems);
    }

    /// <summary>Readiness status and its checks ("database: ...; schema: ...").</summary>
    private static async Task<(string Status, string Detail)> ReadinessAsync(HttpClient http, string baseUrl)
    {
        try
        {
            using var response = await http.GetAsync(baseUrl + "/health/ready");
            var report = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var status = report?["status"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}";
            var checks = (report?["checks"] as JsonArray ?? [])
                .Select(c => $"{c?["name"]?.GetValue<string>()}: {c?["status"]?.GetValue<string>()} - {c?["description"]?.GetValue<string>()}")
                .Take(8);
            var detail = string.Join("; ", checks);
            return (status, detail.Length > 700 ? detail[..700] + "..." : detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ("Unknown", $"/health/ready did not answer: {ex.Message}");
        }
    }
}
