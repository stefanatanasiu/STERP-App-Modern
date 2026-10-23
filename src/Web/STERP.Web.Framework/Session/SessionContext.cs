using System.Globalization;
using System.Text.Json.Nodes;

namespace Sterp.Web.Framework.Session;

/// <summary>One company/site pair the signed-on user may work in (legacy STERP.Common.CompanySite).</summary>
public sealed record AccessEntry(string CompanyCode, string CompanyName, string SiteCode, string SiteName, string Nation, string CalendarCode, string BaseCurrency)
{
    /// <summary>Legacy list text: <c>(CompanyCode + "/" + SiteCode).PadRight(16) + " " + CompanyName [+ " - " + SiteName]</c> (SterpSession.cs:16-19).</summary>
    public string Display => (CompanyCode + "/" + SiteCode).PadRight(16) + " " + CompanyName + (string.IsNullOrEmpty(SiteName) ? "" : " - " + SiteName);

    /// <summary>Selection value 'CO / SITE' of the sign-on page (STERP-PLATFORM.md 7.2).</summary>
    public string Key => CompanyCode + " / " + SiteCode;

    public static AccessEntry From(JsonNode? n) => new(
        Json.Str(n?["companyCode"]), Json.Str(n?["companyName"]), Json.Str(n?["siteCode"]), Json.Str(n?["siteName"]),
        Json.Str(n?["nation"]), Json.Str(n?["calendarCode"]), Json.Str(n?["baseCurrency"]));
}

/// <summary>Fiscal period of the business date (LoginService.LoadPeriod).</summary>
public sealed record FiscalPeriodInfo(int FiscalYear, int PeriodNo, string Status, string PeriodText);

/// <summary>
/// The client-side equivalent of the legacy <c>SterpSession</c> singleton (one per browser tab), built from the PLATFORM
/// session operations. The API re-validates every request (contracts 5.2-5.3).
/// </summary>
public sealed record SessionContext(
    string WindowsLogin,
    string UserCode,
    string DisplayName,
    string RealUserCode,
    bool IsSysAdm,
    string Title,
    string? DefaultCompanyCode,
    string? DefaultSiteCode,
    IReadOnlyList<AccessEntry> Access,
    AccessEntry? Selected,
    DateOnly BusinessDate,
    FiscalPeriodInfo? Period,
    string Message)
{
    public string CompanyCode => Selected?.CompanyCode ?? "";

    public string SiteCode => Selected?.SiteCode ?? "";

    public string CompanyName => Selected?.CompanyName ?? "";

    public string SiteName => Selected?.SiteName ?? "";

    /// <summary>SYSADM demonstrating the system as somebody else (SterpSession.IsImpersonating).</summary>
    public bool IsImpersonating => RealUserCode.Length > 0 && !string.Equals(RealUserCode, UserCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>X-STERP-Run-As value (contracts 4.5): the run-as user while SYSADM continues as another user.</summary>
    public string? RunAs => IsImpersonating ? UserCode : null;

    public string PeriodText => Period?.PeriodText ?? "";

    /// <summary>SterpSession.ContextDiffers: user, real user, company, site or business date changed.</summary>
    public bool ContextDiffers(SessionContext? other) =>
        other is null || !string.Equals(UserCode, other.UserCode, StringComparison.Ordinal) ||
        !string.Equals(RealUserCode, other.RealUserCode, StringComparison.Ordinal) ||
        !string.Equals(CompanyCode, other.CompanyCode, StringComparison.Ordinal) ||
        !string.Equals(SiteCode, other.SiteCode, StringComparison.Ordinal) ||
        BusinessDate != other.BusinessDate;

    /// <summary>Builds the context from a PlatformSessionContext body (platform_getSession / platform_signOn / platform_continueAsUser).</summary>
    public static SessionContext FromJson(JsonNode json)
    {
        var user = json["user"];
        var access = (json["access"] as JsonArray ?? []).Select(AccessEntry.From).ToList();
        var selected = json["selected"] is JsonObject s ? AccessEntry.From(s) : null;
        if (selected is not null)
        {
            selected = access.FirstOrDefault(a => a.CompanyCode == selected.CompanyCode && a.SiteCode == selected.SiteCode) ?? selected;
        }
        FiscalPeriodInfo? period = null;
        if (json["fiscalPeriod"] is JsonObject p)
        {
            period = new FiscalPeriodInfo(Json.Int(p["fiscalYear"]), Json.Int(p["periodNo"]), Json.Str(p["status"]), Json.Str(p["periodText"]));
        }
        var date = DateOnly.TryParseExact(Json.Str(json["businessDate"]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : DateOnly.FromDateTime(DateTime.Today);
        var userCode = Json.Str(user?["userCode"]);
        return new SessionContext(
            Json.Str(json["windowsLogin"]),
            userCode,
            Json.Str(user?["displayName"]) is { Length: > 0 } dn ? dn : userCode,
            Json.Str(json["realUserCode"]) is { Length: > 0 } real ? real : userCode,
            Json.Bool(json["isSysAdm"]),
            Json.Str(json["title"]) is { Length: > 0 } t ? t : userCode,
            Json.NullableStr(user?["defaultCompanyCode"]),
            Json.NullableStr(user?["defaultSiteCode"]),
            access,
            selected,
            date,
            period,
            Json.Str(json["message"]));
    }
}

/// <summary>Holds the signed-on context of this browser tab; pages read it and the shell replaces it on context changes.</summary>
public sealed class SessionService
{
    public SessionContext? Current { get; private set; }

    /// <summary>Permission answers already obtained for button enabling (legacy PermissionCache, cleared on a context change).</summary>
    public Dictionary<string, bool> PermissionCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public void Set(SessionContext? context)
    {
        if (Current is null || context is null || Current.ContextDiffers(context)) PermissionCache.Clear();
        Current = context;
        Changed?.Invoke();
    }
}

/// <summary>Small JSON accessors tolerant of absent or null nodes.</summary>
public static class Json
{
    public static string Str(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : "";

    public static string? NullableStr(JsonNode? n) => n is null ? null : Str(n) is { Length: > 0 } s ? s : null;

    public static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : int.TryParse(Str(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j) ? j : 0;

    public static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var i) ? i
        : long.TryParse(Str(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j) ? j : 0;

    public static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b
        : Str(n) is "1" or "true" or "True" or "Y";

    /// <summary>Fmt.Dec: a JSON number (or numeric text) as decimal; 0 for null or anything else.</summary>
    public static decimal Dec(JsonNode? n) => n is JsonValue v && v.TryGetValue<decimal>(out var d) ? d
        : decimal.TryParse(Str(n), NumberStyles.Any, CultureInfo.InvariantCulture, out var e) ? e : 0m;

    /// <summary>Text of a scalar as serialized (decimals keep their scale); empty for null.</summary>
    public static string Raw(JsonNode? n) => n is null ? "" : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();
}
