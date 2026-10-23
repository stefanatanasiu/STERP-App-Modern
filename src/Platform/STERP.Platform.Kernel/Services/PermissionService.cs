using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>PERMCHKCA body (PERMCHKCA.cpy:17-24).</summary>
public sealed class PermchkArea : Commarea
{
    /// <summary>PK-PERMISSION X(40) - upper-cased by the service.</summary>
    public string Permission { get; set; } = "";

    /// <summary>PK-CHECK-USER X(20) - defaults to CA-USER.</summary>
    public string CheckUser { get; set; } = "";

    /// <summary>PK-CHECK-SITE X(10) - defaults to CA-SITE; blank is sent as NULL (all sites).</summary>
    public string CheckSite { get; set; } = "";

    /// <summary>PK-ALLOWED X: "Y" or "N".</summary>
    public string Allowed { get; set; } = "";

    /// <summary>PK-REASON-TEXT X(200): the procedure's reason (or the failure text).</summary>
    public string ReasonText { get; set; } = "";

    public bool IsAllowed => Allowed == "Y";
}

/// <summary>PERMCHK - authority check for an action, a company and a site (FR-STERP-PLATFORM-012, COM-002).</summary>
public interface IPermissionService
{
    /// <summary>CALL "PERMCHK" USING area: function CHECK (anything else PRM0009 after validation).</summary>
    Task<LegacyResult> ExecuteAsync(PermchkArea area, CancellationToken ct = default);

    /// <summary>Function CHECK: RC 00 allowed, 06 PRM0006 not allowed (a hold, not CA-FAILED), 08 invalid input, 12 SQL failure.</summary>
    Task<PermchkArea> CheckAsync(KernelCaller caller, string permission, string? checkUser = null, string? checkSite = null, CancellationToken ct = default);
}

/// <summary>PERMCHK.cob:49-222 on the caller's engine connection (SQLIO slot 6, autocommit unless the caller began a unit).</summary>
public sealed class PermissionService(ISqlRunner sql) : IPermissionService
{
    public async Task<PermchkArea> CheckAsync(KernelCaller caller, string permission, string? checkUser = null, string? checkSite = null, CancellationToken ct = default)
    {
        var area = new PermchkArea { Permission = permission, CheckUser = checkUser ?? "", CheckSite = checkSite ?? "" };
        area.Header.Prepare(caller, "CHECK");
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<LegacyResult> ExecuteAsync(PermchkArea a, CancellationToken ct = default)
    {
        var h = a.Header;
        // 0100-INITIALISE
        h.Reset("PERMCHK", "COM-002");
        a.Allowed = "N";
        a.ReasonText = "";
        var user = K.X(a.CheckUser, 20);
        if (K.Blank(user, 20)) user = K.X(h.User, 20);
        var site = K.X(a.CheckSite, 10);
        if (CobolText.IsLowValues(site)) site = new string(' ', 10);
        if (CobolText.IsSpaces(site)) site = K.X(h.Site, 10);
        var permission = CobolText.Upper(K.X(a.Permission, 40));
        a.CheckUser = user.TrimEnd(' ');
        a.CheckSite = site.TrimEnd(' ');
        a.Permission = permission.TrimEnd(' ');
        var company = K.X(h.Company, 10);

        // 0200-VALIDATE
        if (K.Blank(user, 20))
        {
            Reject(a, "PRM0001", "A user code is required for an authority check");
            return h.Result;
        }
        if (K.Blank(permission, 40))
        {
            Reject(a, "PRM0002", "A permission code is required");
            return h.Result;
        }
        if (K.Blank(company, 10))
        {
            Reject(a, "PRM0003", "Authority is always scoped to a company");
            return h.Result;
        }

        if (h.FunctionCode != "CHECK")
        {
            Reject(a, "PRM0009", "PERMCHK does not support function " + K.Trim(h.Function));
            return h.Result;
        }

        // 1000-CHECK
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync("EXEC core.usp_CheckPermission ?, ?, ?, ?",
                [SqlioParm.Text(user), SqlioParm.Text(permission), SqlioParm.Text(company),
                 CobolText.IsSpaces(site) ? SqlioParm.Null : SqlioParm.Text(site)],
                [20, 40, 10, 10, 4, 200], ct);
        }
        catch (SqlioException ex)
        {
            // 9300-SQL-FAILED
            a.Allowed = "N";
            h.Message = K.SqlFailureMessage(ex, "PERMCHK");
            a.ReasonText = h.Message;
            if (K.IsBusinessText(h.Message))
            {
                h.ReasonCode = "PRM0007";
                h.ReturnCode = 6;
            }
            else
            {
                h.ReasonCode = "SQL0001";
                h.ReturnCode = 12;
            }
            h.MessageCount++;
            return h.Result;
        }
        if (!row.Found)
        {
            a.Allowed = "N";
            a.ReasonText = "The authority check returned no answer";
            h.Message = a.ReasonText;
            h.ReasonCode = "PRM0005";
            h.ReturnCode = 12;
            return h.Result;
        }
        a.ReasonText = row.Text(5);
        if (row.Raw(4).StartsWith('1'))
        {
            a.Allowed = "Y";
            h.ReturnCode = 0;
            h.Message = K.Msg(K.Trim(user), " holds ", K.Trim(permission), " in ", K.Trim(company));
        }
        else
        {
            // 06 = needs authority, not 08: the request is valid, the user simply may not do it (COM-002 / COM-017)
            a.Allowed = "N";
            h.ReturnCode = 6;
            h.ReasonCode = "PRM0006";
            h.Message = CobolText.IsSpaces(K.X(a.ReasonText, 200))
                ? K.Msg(K.Trim(user), " does not hold ", K.Trim(permission))
                : K.Store(a.ReasonText, 200);
            h.MessageCount++;
        }
        return h.Result;
    }

    private static void Reject(PermchkArea a, string reason, string message)
    {
        a.Header.ReasonCode = reason;
        a.Header.Message = K.Msg(message);
        a.Allowed = "N";
        a.Header.ReturnCode = 8;
        a.Header.MessageCount++;
    }
}
