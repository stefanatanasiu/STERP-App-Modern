using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Web.Framework;
using Sterp.Web.Framework.Api;
using Sterp.Web.Framework.Shell;

namespace Sterp.Web.Pages.So.Support;

/// <summary>
/// The CobolResult a Sales form inspected after a LINK (STERP.CobolInterop/CobolResult.cs): return code, reason code, message
/// and the COMMAREA the facade read whatever the return code was (SalesOrderService.Area). A success carries the COMMAREA as
/// the envelope data, a refusal (RC 8, 12, 16) as the problem's data (D-SO-03). A 404 is the engine's "not found" (RC 16, the
/// bridge-miss answer the screens test with IsNotFound). Any other failure without a return code was an exception of the
/// legacy facade: it is raised as <see cref="ApiFailure"/> for <c>GuardAsync</c>.
/// </summary>
public sealed class SoResult
{
    private SoResult(int returnCode, string reasonCode, string message, JsonNode? area)
    {
        ReturnCode = returnCode;
        ReasonCode = reasonCode;
        Message = message;
        Area = area;
    }

    public int ReturnCode { get; }

    public string ReasonCode { get; }

    public string Message { get; }

    /// <summary>The COMMAREA body of the answer (null when the operation returned none).</summary>
    public JsonNode? Area { get; }

    public bool IsWarning => ReturnCode == 4;

    public bool IsHold => ReturnCode == 6;

    public bool IsNotFound => ReturnCode == 16;

    /// <summary>True when the engine accepted the request (00 or 04).</summary>
    public bool Succeeded => ReturnCode <= 4;

    public string Severity => ReturnCode switch
    {
        0 => "OK",
        4 => "WARNING",
        6 => "HOLD",
        8 => "ERROR",
        12 => "SEVERE",
        16 => "NOT FOUND",
        _ => "RC" + ReturnCode.ToString("00", CultureInfo.InvariantCulture),
    };

    /// <summary>CobolResult.Display: the message (or the severity) with the reason code in brackets (COM-017).</summary>
    public string Display
    {
        get
        {
            var m = Message.Trim();
            if (m.Length == 0) m = Severity;
            return ReasonCode.Trim().Length > 0 ? m + "  (" + ReasonCode.Trim() + ")" : m;
        }
    }

    public static SoResult From(ApiResponse response)
    {
        if (response.IsSuccess)
        {
            var o = response.Outcome;
            return new SoResult(o?.ReturnCode ?? 0, o?.ReasonCode ?? "", o?.Message ?? "", response.Data);
        }
        var p = response.Problem!;
        if (p.ReturnCode is { } rc) return new SoResult(rc, p.ReasonCode, p.Detail, p.Data);
        if (response.Status == 404) return new SoResult(16, p.ReasonCode, p.Detail, p.Data);
        throw new ApiFailure(response);
    }
}

/// <summary>The legacy window size of a form (ClientSize) kept inside the workspace as BaseForm.FitToWorkspace did.</summary>
public static class SoWindow
{
    /// <summary>Id of the shell's MDI client area (STERP.Web.Client ShellService.WorkspaceId).</summary>
    private const string WorkspaceId = "st-workspace";

    public static void Size(WindowContext window, BrowserInterop browser, int width, int height)
    {
        var (aw, ah) = browser.ElementSize(WorkspaceId);
        if (aw <= 0 || ah <= 0) return;
        window.Width = Math.Max(Math.Min(width, aw - 4), 320);
        window.Height = Math.Max(Math.Min(height, ah - 4), 220);
        window.X = Math.Max(0, Math.Min(window.X, aw - window.Width));
        window.Y = Math.Max(0, Math.Min(window.Y, ah - window.Height));
        window.NotifyChanged();
    }
}
