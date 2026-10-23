using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Platform.Session;

/// <summary>Fiscal period of a company containing a date (LoginService.LoadPeriod).</summary>
public sealed record FiscalPeriod(int FiscalYear, int PeriodNo, string Status)
{
    /// <summary>Fmt.Period (Fmt.cs:105-109): yyyy/pp, '' when the year is not positive.</summary>
    public string PeriodText => FiscalYear <= 0 ? "" :
        FiscalYear.ToString("0000", CultureInfo.InvariantCulture) + "/" + PeriodNo.ToString("00", CultureInfo.InvariantCulture);

    public JsonObject ToJson() => new()
    {
        ["fiscalYear"] = FiscalYear,
        ["periodNo"] = PeriodNo,
        ["status"] = Status,
        ["periodText"] = PeriodText,
    };
}

/// <summary>
/// The session context the legacy SterpSession held after LoginService.SignOnAs (SterpSession.cs; LoginService.vb:23-74,
/// 161-179; frmLogin.vb:43-70), built per request from the resolved STERP user.
/// </summary>
public static class SessionContext
{
    public const string SysAdm = "SYSADM";

    /// <summary>
    /// LoginService.LoadPeriod (LoginService.vb:161-179): first fin.Period row of the company containing the date, ordered by
    /// FiscalYear, PeriodNo, on its own SqlHelper connection; no row or a failure gives no period (failure logged 'LoadPeriod: ').
    /// </summary>
    public static async Task<FiscalPeriod?> LoadPeriodAsync(IProcedureRunner sql, string? companyCode, DateOnly businessDate, ILogger logger, CancellationToken ct)
    {
        try
        {
            var row = await sql.QueryFirstOrDefaultAsync<PeriodRow>(
                "SELECT TOP 1 FiscalYear, PeriodNo, Status FROM fin.Period " +
                "WHERE CompanyCode = @c AND @d BETWEEN StartDate AND EndDate ORDER BY FiscalYear, PeriodNo",
                new Dictionary<string, object?> { ["@c"] = companyCode, ["@d"] = businessDate.ToDateTime(TimeOnly.MinValue) }, ct);
            return row is null ? null : new FiscalPeriod(row.FiscalYear, row.PeriodNo, (row.Status ?? "").Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("LoadPeriod: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// AuditService.Note (AuditService.vb:15-25): core.usp_WriteAudit with EntityType 'SESSION' and Source 'STERP.exe' on its own
    /// connection; never throws (a failure is logged 'WriteAudit &lt;action&gt;: ').
    /// </summary>
    public static async Task NoteAsync(IProcedureRunner sql, string userCode, string? companyCode, string action, string entityKey, string reason,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            await sql.ExecuteAsync("core.usp_WriteAudit", new
            {
                UserCode = userCode,
                CompanyCode = companyCode,
                EntityType = "SESSION",
                EntityKey = entityKey,
                Action = action,
                Reason = reason,
                Source = "STERP.exe",
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("WriteAudit {Action}: {Message}", action, ex.Message);
        }
    }

    /// <summary>The access-list entry selected by company (and site when given), else null.</summary>
    public static CompanySiteAccess? Find(SterpUser user, string? company, string? site)
    {
        if (string.IsNullOrWhiteSpace(company)) return null;
        return user.Access.FirstOrDefault(a =>
            string.Equals(a.CompanyCode, company.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(site) || string.Equals(a.SiteCode, site.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>PlatformSessionContext for the effective user, the real user and the selected access entry.</summary>
    public static JsonObject Build(string windowsLogin, SterpUser user, SterpUser realUser, CompanySiteAccess? selected, DateOnly businessDate,
        FiscalPeriod? period)
    {
        var impersonating = realUser.UserCode.Length > 0 && !string.Equals(realUser.UserCode, user.UserCode, StringComparison.Ordinal);
        var title = user.UserCode + (impersonating ? " (as " + realUser.UserCode + ")" : "");
        var access = new JsonArray(user.Access.Select(a => (JsonNode?)Access(a)).ToArray());
        return new JsonObject
        {
            ["windowsLogin"] = windowsLogin,
            ["user"] = new JsonObject
            {
                ["userCode"] = user.UserCode,
                ["displayName"] = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserCode : user.DisplayName,
                ["windowsLogin"] = user.WindowsLogin,
                ["emailAddress"] = user.EmailAddress,
                ["jobTitle"] = user.JobTitle,
                ["defaultCompanyCode"] = user.DefaultCompanyCode,
                ["defaultSiteCode"] = user.DefaultSiteCode,
                ["isReadOnly"] = user.IsReadOnly,
                ["isActive"] = user.IsActive,
                ["businessDate"] = SqlJson.Date(user.ServerDate),
            },
            ["realUserCode"] = realUser.UserCode,
            ["isSysAdm"] = string.Equals(user.UserCode, SysAdm, StringComparison.Ordinal),
            ["isImpersonating"] = impersonating,
            ["title"] = title,
            ["access"] = access,
            ["selected"] = selected is null ? null : Access(selected),
            ["businessDate"] = SqlJson.Date(businessDate),
            ["fiscalPeriod"] = period?.ToJson(),
            ["message"] = user.Access.Count == 0
                ? "*** No company or site has been granted to this user."
                : user.Access.Count.ToString(CultureInfo.InvariantCulture) + " company/site combination(s) available.",
        };
    }

    public static JsonObject Access(CompanySiteAccess a) => new()
    {
        ["companyCode"] = a.CompanyCode,
        ["companyName"] = a.CompanyName,
        ["siteCode"] = a.SiteCode,
        ["siteName"] = a.SiteName,
        ["nation"] = a.Nation,
        ["calendarCode"] = a.CalendarCode,
        ["baseCurrency"] = a.BaseCurrency,
    };

    private sealed record PeriodRow(short FiscalYear, byte PeriodNo, string? Status);
}
