using System.Text.Json.Nodes;
using Sterp.Web.Framework;
using Sterp.Web.Framework.Api;
using Sterp.Web.Framework.Session;
using Sterp.Web.Framework.Shell;

namespace Sterp.Web.Pages.Ar.Support;

/// <summary>
/// LoginService.HasPermission for button enabling (LoginService.vb:143-158): a blank code is allowed; the answers are cached
/// for the company/site selection (SessionService.PermissionCache); a failed probe counts as allowed (fail-open, as the
/// legacy catch did). Probe: platform_checkPermissions.
/// </summary>
public static class ArPermissions
{
    public static async Task<bool> HasAsync(ApiClient api, SessionService session, string permissionCode)
    {
        if (string.IsNullOrEmpty(permissionCode)) return true;
        if (session.PermissionCache.TryGetValue(permissionCode, out var cached)) return cached;
        var allowed = true;
        var r = await api.PostAsync("/api/platform/v1/session/permission-checks",
            new JsonObject { ["permissionCodes"] = new JsonArray(permissionCode) });
        if (r.IsSuccess)
        {
            var item = (r.Json?["items"] as JsonArray ?? []).FirstOrDefault(i =>
                string.Equals(Json.Str(i?["permissionCode"]), permissionCode, StringComparison.OrdinalIgnoreCase));
            allowed = item is not null && Json.Bool(item["allowed"]);
        }
        session.PermissionCache[permissionCode] = allowed;
        return allowed;
    }
}

/// <summary>The legacy window size of a form (ClientSize) kept inside the workspace as BaseForm.FitToWorkspace did.</summary>
public static class ArWindow
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
