using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Platform.Session;

/// <summary>
/// Sign-on, session context, SYSADM run-as, run-as user list and permission probe (FR-STERP-PLATFORM-031..033; STERP-PLATFORM
/// 4.3). Legacy: frmLogin, LoginService, AuditService.Note, SterpSession.
/// </summary>
public static class SessionEndpoints
{
    private const string OnlySysAdm = "Only SYSADM may continue as another user.";

    public static void Map(OperationRegistry ops)
    {
        ops.Handle("platform_getSession", GetSessionAsync);
        ops.Handle("platform_signOn", SignOnAsync);
        ops.Handle("platform_continueAsUser", ContinueAsUserAsync);
        ops.Handle("platform_listRunAsUsers", ListRunAsUsersAsync);
        ops.Handle("platform_checkPermissions", CheckPermissionsAsync);
    }

    private static ILogger Log(OperationContext ctx) => ctx.Get<ILoggerFactory>().CreateLogger("Sterp.Platform.Session");

    /// <summary>
    /// platform_getSession: the context of LoginService.SignOnAs - the headers' company/site when present in the access list
    /// (the pipeline refused any other with 403 API0002), otherwise the first entry (SelectCompanySite(Access(0))); the
    /// fiscal period of that company for the business date.
    /// </summary>
    private static async Task<IResult> GetSessionAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        var selected = SessionContext.Find(s.User, s.CompanyCode, s.SiteCode) ?? s.User.Access.FirstOrDefault();
        var period = await SessionContext.LoadPeriodAsync(ctx.Get<IProcedureRunner>(), selected?.CompanyCode ?? "", s.BusinessDate, Log(ctx), ctx.Aborted);
        return ctx.Direct(SessionContext.Build(s.WindowsLogin, s.User, s.RealUser, selected, s.BusinessDate, period));
    }

    /// <summary>
    /// platform_signOn (frmLogin.btnOk_Click, frmLogin.vb:81-93): the chosen company/site must be an access-list entry (403
    /// API0002), the period is loaded for the chosen business date and the SIGNON note is written best effort.
    /// </summary>
    private static async Task<IResult> SignOnAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var company = RequiredText(body, "companyCode", 10);
        var site = RequiredText(body, "siteCode", 10);
        var dateText = RequiredText(body, "businessDate", 10);
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var businessDate))
        {
            throw ApiProblemException.BadBody("businessDate must be an ISO date (yyyy-MM-dd).");
        }
        var s = ctx.Session;
        var chosen = s.User.Access.FirstOrDefault(a =>
            string.Equals(a.CompanyCode, company, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.SiteCode, site, StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            return ctx.Forbidden(ApiReasons.CompanySiteNotAllowed, $"{s.UserCode} has no access to {company.ToUpperInvariant()}/{site.ToUpperInvariant()}.");
        }
        var sql = ctx.Get<IProcedureRunner>();
        var log = Log(ctx);
        var period = await SessionContext.LoadPeriodAsync(sql, chosen.CompanyCode, businessDate, log, ctx.Aborted);
        await SessionContext.NoteAsync(sql, s.UserCode, chosen.CompanyCode, "SIGNON", s.UserCode,
            "Signed on to " + chosen.CompanyCode + "/" + chosen.SiteCode, log, ctx.Aborted);
        return ctx.Direct(SessionContext.Build(s.WindowsLogin, s.User, s.RealUser, chosen, businessDate, period));
    }

    /// <summary>
    /// platform_continueAsUser (LoginService.RunAs, LoginService.vb:129-140; frmLogin.vb:72-79): real user SYSADM only (403
    /// API0003), the chosen code resolved with core.usp_GetUserContext @UserCode (403 API0010 when unknown or inactive), the
    /// RUNAS note written best effort; choosing the user already in effect does nothing.
    /// </summary>
    private static async Task<IResult> ContinueAsUserAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        if (!string.Equals(s.RealUserCode, SessionContext.SysAdm, StringComparison.Ordinal))
        {
            return ctx.Forbidden(ApiReasons.RunAsNotSysAdm, OnlySysAdm);
        }
        var body = await ctx.BodyObjectAsync();
        var code = RequiredText(body, "userCode", 20);
        var sql = ctx.Get<IProcedureRunner>();
        var log = Log(ctx);
        if (string.Equals(code, s.UserCode, StringComparison.Ordinal))
        {
            var current = SessionContext.Find(s.User, s.CompanyCode, s.SiteCode) ?? s.User.Access.FirstOrDefault();
            var currentPeriod = await SessionContext.LoadPeriodAsync(sql, current?.CompanyCode ?? "", s.BusinessDate, log, ctx.Aborted);
            return ctx.Direct(SessionContext.Build(s.WindowsLogin, s.User, s.RealUser, current, s.BusinessDate, currentPeriod));
        }
        var target = await ctx.Get<UserContextResolver>().ByUserCodeAsync(code, ctx.Aborted);
        if (target is null)
        {
            return ctx.Forbidden(ApiReasons.NoUserMapped, $"No STERP user is mapped to {code}.");
        }
        // SignOnAs selects Access(0); the sign-on page keeps the selection the operator had when the new user holds it
        var first = target.Access.FirstOrDefault();
        await SessionContext.NoteAsync(sql, target.UserCode, first?.CompanyCode ?? s.CompanyCode, "RUNAS", code,
            "SYSADM continued the session as " + code, log, ctx.Aborted);
        var selected = SessionContext.Find(target, s.CompanyCode, s.SiteCode) ?? first;
        var period = await SessionContext.LoadPeriodAsync(sql, selected?.CompanyCode ?? "", s.BusinessDate, log, ctx.Aborted);
        return ctx.Direct(SessionContext.Build(s.WindowsLogin, target, s.RealUser, selected, s.BusinessDate, period));
    }

    /// <summary>
    /// platform_listRunAsUsers (LoginService.UserList, LoginService.vb:182-189): offered only to SYSADM (403 API0003); a read
    /// failure gives an empty list as legacy.
    /// </summary>
    private static async Task<IResult> ListRunAsUsersAsync(OperationContext ctx)
    {
        if (!string.Equals(ctx.Session.RealUserCode, SessionContext.SysAdm, StringComparison.Ordinal))
        {
            return ctx.Forbidden(ApiReasons.RunAsNotSysAdm, OnlySysAdm);
        }
        JsonArray rows;
        try
        {
            rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync(
                "SELECT UserCode, DisplayName FROM core.AppUser WHERE IsActive = 1 ORDER BY UserCode", null, ctx.Aborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(ctx).LogWarning("UserList: {Message}", ex.Message);
            rows = [];
        }
        foreach (var r in rows.OfType<JsonObject>())
        {
            r["userCode"] = (r["userCode"]?.GetValue<string>() ?? "").Trim();
        }
        return ctx.List(rows);
    }

    /// <summary>
    /// platform_checkPermissions (LoginService.HasPermission, LoginService.vb:143-158): a null or empty code is allowed without
    /// a call; otherwise core.usp_CheckPermission for the session user, company and site, allowed = the row's Allowed bit.
    /// </summary>
    private static async Task<IResult> CheckPermissionsAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        if (body["permissionCodes"] is not JsonArray codes || codes.Count == 0)
        {
            throw ApiProblemException.BadBody("permissionCodes must be a non-empty array of permission codes.");
        }
        if (codes.Count > 100) throw ApiProblemException.BadBody("permissionCodes holds at most 100 codes.");
        var s = ctx.Session;
        var sql = ctx.Get<IProcedureRunner>();
        var items = new JsonArray();
        foreach (var node in codes)
        {
            var code = node is null ? null : node is JsonValue v && v.TryGetValue<string>(out var t)
                ? t
                : throw ApiProblemException.BadBody("permissionCodes must hold strings.");
            if (string.IsNullOrEmpty(code))
            {
                items.Add(new JsonObject { ["permissionCode"] = code ?? "", ["allowed"] = true, ["checked"] = false, ["reason"] = null });
                continue;
            }
            var rows = await sql.QueryJsonAsync("core.usp_CheckPermission", new
            {
                UserCode = s.UserCode,
                PermissionCode = code,
                CompanyCode = s.CompanyCode,
                SiteCode = s.SiteCode,
            }, ctx.Aborted);
            var row = rows.Count > 0 ? rows[0] as JsonObject : null;
            var allowed = row?["allowed"] is JsonValue a && a.TryGetValue<bool>(out var b) && b;
            items.Add(new JsonObject
            {
                ["permissionCode"] = code,
                ["allowed"] = allowed,
                ["checked"] = true,
                ["reason"] = row?["reason"]?.DeepClone(),
            });
        }
        return ctx.List(items);
    }

    internal static string RequiredText(JsonObject body, string name, int maxLength)
    {
        var node = body[name];
        var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw ApiProblemException.BadBody($"{name} is required.", new Dictionary<string, string[]> { [name] = [$"{name} is required."] });
        }
        text = text.Trim();
        if (text.Length > maxLength)
        {
            throw ApiProblemException.BadBody($"{name} is at most {maxLength} characters.", new Dictionary<string, string[]> { [name] = [$"at most {maxLength} characters"] });
        }
        return text;
    }
}
