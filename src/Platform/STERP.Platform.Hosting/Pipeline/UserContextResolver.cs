using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Hosting.Pipeline;

public sealed class SterpHostOptions
{
    public const string Section = "Sterp:Host";

    /// <summary>"online" (STERP.Api) or "batch" (STERP.Jobs) - the legacy two engines (governance rule 8).</summary>
    public string Engine { get; set; } = "online";

    public string ProductName { get; set; } = "STERP - Supply Chain Management";

    /// <summary>User-context cache (contracts 5.2 allows at most 60 s); 0 = resolve on every request.</summary>
    public int UserContextCacheSeconds { get; set; }

    /// <summary>Credentialed CORS origins (contracts 5.7): STERP-WEB and STERP-PORTAL.</summary>
    public string[] CorsOrigins { get; set; } = ["https://localhost:7100", "https://localhost:7103"];

    /// <summary>
    /// Slices to treat as not delivered even though their module is present (verifies FR-STERP-API-002 / FR-STERP-JOBS-002
    /// wave-transition behaviour); empty in normal operation.
    /// </summary>
    public string[] UndeliveredSlices { get; set; } = [];

    /// <summary>Request/response body logging for diagnostics (off by default; contracts 5.9).</summary>
    public bool LogBodies { get; set; }
}

/// <summary>
/// STERP user resolution per request (contracts 5.2): core.usp_GetUserContext @WindowsLogin (result set 1 = user, result set 2 =
/// company/site access list) exactly as legacy sign-on (LoginService.vb:23-60); SYSADM run-as through @UserCode.
/// </summary>
public sealed class UserContextResolver(IProcedureRunner sql, IMemoryCache cache, IOptions<SterpHostOptions> options)
{
    private readonly int _cacheSeconds = Math.Clamp(options.Value.UserContextCacheSeconds, 0, 60);

    /// <summary>Resolves a Windows login; null when no active STERP user is mapped.</summary>
    public Task<SterpUser?> ByLoginAsync(string windowsLogin, CancellationToken ct) =>
        CachedAsync("login:" + windowsLogin, () => LoadAsync(new { WindowsLogin = windowsLogin }, ct));

    /// <summary>Resolves a STERP user code (run-as); null when not an active user.</summary>
    public Task<SterpUser?> ByUserCodeAsync(string userCode, CancellationToken ct) =>
        CachedAsync("user:" + userCode.ToUpperInvariant(), () => LoadAsync(new { UserCode = userCode }, ct));

    private async Task<SterpUser?> CachedAsync(string key, Func<Task<SterpUser?>> load)
    {
        if (_cacheSeconds == 0) return await load();
        if (cache.TryGetValue(key, out SterpUser? hit)) return hit;
        var user = await load();
        if (user is not null) cache.Set(key, user, TimeSpan.FromSeconds(_cacheSeconds));
        return user;
    }

    private async Task<SterpUser?> LoadAsync(object param, CancellationToken ct)
    {
        List<JsonArray> sets;
        try
        {
            sets = await sql.QueryJsonSetsAsync("core.usp_GetUserContext", param, ct);
        }
        catch (BusinessRuleException ex) when (ex.Code == "E-0002")
        {
            return null; // STERP-E-0002: no active STERP user is mapped to this sign-on
        }
        if (sets.Count == 0 || sets[0].Count == 0) return null;
        var u = (JsonObject)sets[0][0]!;
        var access = sets.Count > 1
            ? sets[1].Select(a => new CompanySiteAccess(
                Str(a, "companyCode"), Str(a, "companyName"), Str(a, "siteCode"), Str(a, "siteName"),
                StrOrNull(a, "nation"), StrOrNull(a, "calendarCode"), StrOrNull(a, "baseCurrency"))).ToList()
            : [];
        var serverDate = DateOnly.TryParse(Str(u, "businessDate"), System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : DateOnly.FromDateTime(DateTime.Today);
        return new SterpUser(
            Str(u, "userCode"), Str(u, "displayName"), StrOrNull(u, "windowsLogin"), StrOrNull(u, "emailAddress"),
            StrOrNull(u, "jobTitle"), StrOrNull(u, "defaultCompanyCode"), StrOrNull(u, "defaultSiteCode"),
            Bool(u, "isReadOnly"), Bool(u, "isActive"), serverDate, access);
    }

    private static string Str(JsonNode? n, string name) => StrOrNull(n, name) ?? "";

    private static string? StrOrNull(JsonNode? n, string name) => n?[name] is JsonValue v ? v.ToString() : null;

    private static bool Bool(JsonNode? n, string name) => n?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
