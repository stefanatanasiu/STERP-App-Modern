using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace STERP.Testing;

/// <summary>Thrown when a target host is not reachable: the expected red state until Agent 08 delivers it.</summary>
public sealed class TargetNotImplementedException(string message) : Exception(message);

/// <summary>Raised for a failed parity expectation.</summary>
public sealed class ParityAssertionException : Exception
{
    public ParityAssertionException(string message) : base(message) { }

    public ParityAssertionException(string message, Exception inner) : base(message, inner) { }
}

public sealed record HttpOutcome(int Status, HttpResponseHeaders Headers, HttpContentHeaders? ContentHeaders, JsonNode? Json, string Text)
{
    public JsonObject ToNode()
    {
        var node = new JsonObject
        {
            ["status"] = Status,
            ["json"] = Json?.DeepClone(),
            ["text"] = Text,
        };
        var headers = new JsonObject();
        foreach (var h in Headers) headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
        if (ContentHeaders is not null)
        {
            foreach (var h in ContentHeaders) headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
        }
        node["headers"] = headers;
        return node;
    }
}

/// <summary>HTTP access to the target hosts with the caller's own Windows credentials (contracts 5.1).</summary>
public static class TargetClient
{
    private static readonly ConcurrentDictionary<string, string?> Reachability = new(StringComparer.OrdinalIgnoreCase);

    public static HttpClient Http { get; } = new(new HttpClientHandler
    {
        UseDefaultCredentials = true,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true,
    })
    {
        Timeout = TimeSpan.FromSeconds(130),
    };

    /// <summary>Fails fast (expected red) when the host has not been built or started yet.</summary>
    public static async Task EnsureReachableAsync(string baseUrl, string what)
    {
        var reason = await Reachability.GetOrAddAsync(baseUrl, async url =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await Http.GetAsync(new Uri(new Uri(url), "/health/live"), cts.Token);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetBaseException().Message;
            }
        });
        if (reason is not null)
        {
            throw new TargetNotImplementedException(
                $"RED-UNTIL-IMPLEMENTED: {what} at {baseUrl} is not reachable ({reason}). " +
                "Agent 08 has not delivered this target capability yet; this parity test is expected to fail until it does.");
        }
    }

    public static async Task<HttpOutcome> SendAsync(HttpMethod method, string url, IDictionary<string, string> headers, JsonNode? body)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        var media = response.Content.Headers.ContentType?.MediaType ?? "";
        if (media.Contains("json", StringComparison.OrdinalIgnoreCase) && text.Length > 0)
        {
            try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { json = null; }
        }
        return new HttpOutcome((int)response.StatusCode, response.Headers, response.Content.Headers, json, text);
    }

    public static async Task<HttpStatusCode> GetStatusAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        return response.StatusCode;
    }
}

internal static class ConcurrentDictionaryExtensions
{
    public static async Task<TValue> GetOrAddAsync<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dict, TKey key, Func<TKey, Task<TValue>> factory)
        where TKey : notnull
    {
        if (dict.TryGetValue(key, out var existing)) return existing;
        var value = await factory(key);
        return dict.GetOrAdd(key, value);
    }
}
