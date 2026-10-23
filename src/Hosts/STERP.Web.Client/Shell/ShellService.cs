using System.Text.Json.Nodes;
using Sterp.Web.Framework;
using Sterp.Web.Framework.Api;
using Sterp.Web.Framework.Dialogs;
using Sterp.Web.Framework.Format;
using Sterp.Web.Framework.Registry;
using Sterp.Web.Framework.Session;
using Sterp.Web.Framework.Shell;
using Sterp.Web.Pages.Platform.Pages;

namespace Sterp.Web.Client.Shell;

public enum ShellPhase
{
    Starting,
    Ready,
    Ended,
}

/// <summary>One core.MenuItem row as returned by platform_getMenu (core.usp_UiMenuLoad).</summary>
public sealed record MenuRow(string MenuCode, string? ParentMenuCode, string Caption, string? FormClass, string? FormArgument, string? Shortcut, int SortOrder);

/// <summary>A menu entry with its children (ToolStripMenuItem).</summary>
public sealed class MenuNode(MenuRow row)
{
    public MenuRow Row { get; } = row;

    public List<MenuNode> Children { get; } = [];

    public string Code => Row.MenuCode;

    public string Caption => Row.Caption;

    public string Text => UkFormat.StripMnemonic(Row.Caption);

    public char? AccessKey => UkFormat.Mnemonic(Row.Caption);

    public string Shortcut => Row.Shortcut ?? "";

    /// <summary>The window list of the WINDOW menu (MdiWindowListItem): activates an open window.</summary>
    public WindowContext? Window { get; init; }
}

/// <summary>
/// The STERP-WEB shell (legacy frmMain + Program, FR-STERP-WEB-002/010-020): start-up and sign-on, menu bar from
/// platform_getMenu with the built-in fallback, toolbar, status bar, the MDI-like workspace, shell commands, context
/// changes and the page registry (FormClass -> page or placeholder).
/// </summary>
public sealed partial class ShellService(
    ApiClient api,
    SessionService session,
    DialogService dialogs,
    BrowserInterop browser,
    PageRegistry registry,
    SterpClientOptions options,
    ILogger<ShellService> logger) : IShell
{
    private HashSet<string> _delivered = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _statusTimer;

    public ShellPhase Phase { get; private set; } = ShellPhase.Starting;

    /// <summary>Start-up progress lines (frmSplash.ShowStep).</summary>
    public List<string> Steps { get; } = [];

    public bool StartFailed { get; private set; }

    public List<MenuNode> Menu { get; private set; } = [];

    public string Message { get; private set; } = "";

    /// <summary>Service status shown as "Engine: &lt;status&gt;" (platform_getServiceStatus, readiness fallback).</summary>
    public string EngineStatus { get; private set; } = "";

    public bool CapsLock { get; set; }

    public bool NumLock { get; set; }

    public string EndedText { get; private set; } = "";

    public SterpClientOptions Options => options;

    public SessionContext? Session => session.Current;

    public IReadOnlyCollection<string> DeliveredSlices => _delivered.Select(s => "STERP-" + s).ToList();

    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    // ----------------------------------------------------------------------------------------------- start-up

    /// <summary>Program.Main: splash and service checks, automatic sign-on, menu, then the deep-linked page.</summary>
    public async Task StartAsync(string path, string? query)
    {
        try
        {
            if (!await CheckServicesAsync()) return;
            var signedOn = await SignOnAtStartAsync();
            if (signedOn is null) return;
            session.Set(signedOn);
            await LoadMenuAsync();
            Phase = ShellPhase.Ready;
            ApplyTitle();
            Message = "Ready.";
            Changed?.Invoke();
            await Task.Yield();
            OpenDeepLink(path, query);
            _ = RefreshEngineStatusAsync();
            _statusTimer = new Timer(_ => _ = RefreshEngineStatusAsync(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Start-up failed");
            Steps.Add("STERP-E-9999: An unexpected error has occurred. " + ex.Message);
            StartFailed = true;
            Changed?.Invoke();
        }
        finally
        {
            await Task.Delay(50);
            browser.StartupDone();
        }
    }

    /// <summary>Readiness of STERP.Api and STERP.Jobs replaces the legacy engine start (Program.vb:36-47, FR-STERP-WEB-010).</summary>
    private async Task<bool> CheckServicesAsync()
    {
        Step("Checking the online service STERP.Api...");
        var apiReady = await api.GetAbsoluteAsync(options.ApiBaseUrl.TrimEnd('/') + "/health/ready");
        if (apiReady.NetworkError is not null || apiReady.Json is null)
        {
            Fail($"STERP.Api is not available at {options.ApiBaseUrl}: {apiReady.NetworkError ?? "HTTP " + apiReady.Status}. Start the service, then reload this page.");
            return false;
        }
        _delivered = new HashSet<string>((apiReady.Json["deliveredSlices"] as JsonArray ?? []).Select(Json.Str), StringComparer.OrdinalIgnoreCase);
        Replace($"Online service: {Json.Str(apiReady.Json["status"])} ({_delivered.Count} slice(s) delivered)");

        Step("Checking the batch service STERP.Jobs...");
        var jobsReady = await api.GetAbsoluteAsync(options.JobsBaseUrl.TrimEnd('/') + "/health/ready");
        if (jobsReady.NetworkError is not null || jobsReady.Json is null)
        {
            Fail($"STERP.Jobs is not available at {options.JobsBaseUrl}: {jobsReady.NetworkError ?? "HTTP " + jobsReady.Status}. Start the service, then reload this page.");
            return false;
        }
        Replace($"Batch service: {Json.Str(jobsReady.Json["status"])}");
        EngineStatus = Json.Str(apiReady.Json["status"]) == "Healthy" && Json.Str(jobsReady.Json["status"]) == "Healthy" ? "ONLINE" : "DEGRADED";

        Step("Connecting to the STERP database...");
        var db = (apiReady.Json["checks"] as JsonArray ?? []).FirstOrDefault(c => Json.Str(c?["name"]) == "database");
        Replace("Database: " + (Json.Str(db?["description"]) is { Length: > 0 } d ? d : Json.Str(db?["status"])));
        return true;
    }

    /// <summary>
    /// frmLogin at start-up: the Windows login is resolved by platform_getSession and the session is confirmed with
    /// platform_signOn for the user's default company/site (first entry otherwise) and today's business date.
    /// </summary>
    private async Task<SessionContext?> SignOnAtStartAsync()
    {
        Step("Signing on...");
        var r = await api.GetAsync("/api/platform/v1/session", new ApiCallOptions { NoContext = true });
        if (!r.IsSuccess || r.Json is null)
        {
            // no mapped user (403 API0010) or sign-on unavailable: the sign-on page shows the message; only Cancel remains
            var text = r.Problem?.Detail ?? "Sign-on failed.";
            var result = await dialogs.ShowAsync(typeof(SignOnDialog), new Dictionary<string, object?>
            {
                [nameof(SignOnDialog.StartupError)] = text,
            });
            if (result is SessionContext ok) return ok;
            End("Sign-on cancelled.");
            return null;
        }
        var ctx = SessionContext.FromJson(r.Json);
        var pick = ctx.Access.FirstOrDefault(a => a.CompanyCode == ctx.DefaultCompanyCode && a.SiteCode == ctx.DefaultSiteCode)
                   ?? ctx.Access.FirstOrDefault();
        if (pick is null)
        {
            var result = await dialogs.ShowAsync(typeof(SignOnDialog), new Dictionary<string, object?>
            {
                [nameof(SignOnDialog.StartupError)] = "No company or site has been granted to this user.",
            });
            if (result is SessionContext ok) return ok;
            End("Sign-on cancelled.");
            return null;
        }
        var signOn = await api.PostAsync("/api/platform/v1/session/sign-on", new JsonObject
        {
            ["companyCode"] = pick.CompanyCode,
            ["siteCode"] = pick.SiteCode,
            ["businessDate"] = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", UkFormat.Culture),
        }, new ApiCallOptions { NoContext = true, IdempotencyKey = Guid.NewGuid().ToString() });
        if (!signOn.IsSuccess || signOn.Json is null)
        {
            Fail("Sign-on failed: " + (signOn.Problem?.Detail ?? "HTTP " + signOn.Status));
            return null;
        }
        Replace("Signed on.");
        return SessionContext.FromJson(signOn.Json);
    }

    private void Step(string text)
    {
        Steps.Add(text);
        Changed?.Invoke();
    }

    private void Replace(string text)
    {
        if (Steps.Count > 0) Steps[^1] = text; else Steps.Add(text);
        Changed?.Invoke();
    }

    private void Fail(string text)
    {
        Steps.Add(text);
        StartFailed = true;
        Changed?.Invoke();
    }

    private void End(string text)
    {
        Phase = ShellPhase.Ended;
        EndedText = text;
        _statusTimer?.Dispose();
        Changed?.Invoke();
    }

    /// <summary>
    /// The browser title: the frmMain title (frmMain.vb:24), followed by the active child window's caption in the form the MDI
    /// frame shows for a maximised child (<c>&lt;main title&gt; - [&lt;window title&gt;]</c>). The legacy frame adds the caption only
    /// while the active child is maximised; the browser has a single document title, so it names the active window whenever
    /// one is shown (contracts 9.6, WCAG 2.4.2) - a minimised or closed window adds nothing, as in the legacy frame.
    /// </summary>
    private void ApplyTitle()
    {
        var title = Title;
        if (ActiveWindow is { Closed: false, State: not WindowState.Minimized } active && active.Title.Length > 0)
        {
            title += " - [" + active.Title + "]";
        }
        if (title == _appliedTitle) return;
        _appliedTitle = title;
        browser.SetTitle(title);
    }

    private string _appliedTitle = "";

    /// <summary>frmMain title: "&lt;product&gt;  [&lt;company&gt;] &lt;company name&gt;".</summary>
    public string Title => session.Current is { } s ? options.ProductName + "  [" + s.CompanyCode + "] " + s.CompanyName : options.ProductName;

    // ----------------------------------------------------------------------------------------- engine status

    /// <summary>frmMain.tmrStatus: the service status every 60 s (EngineService.Status semantics).</summary>
    public async Task RefreshEngineStatusAsync()
    {
        try
        {
            var r = await api.GetAsync("/api/platform/v1/service-status", new ApiCallOptions { NoContext = true });
            if (r.IsSuccess && Json.Str(r.Json?["status"]) is { Length: > 0 } status)
            {
                EngineStatus = status;
            }
            else
            {
                var ready = await api.GetAbsoluteAsync(options.ApiBaseUrl.TrimEnd('/') + "/health/ready");
                EngineStatus = ready.NetworkError is not null ? "DOWN" : Json.Str(ready.Json?["status"]) == "Healthy" ? "ONLINE" : "DEGRADED";
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Service status");
            EngineStatus = "DOWN";
        }
        Changed?.Invoke();
    }

    // -------------------------------------------------------------------------------------------------- menu

    /// <summary>frmMain.BuildMenu: platform_getMenu rows (already permission filtered) or the built-in fallback menu.</summary>
    public async Task LoadMenuAsync()
    {
        var rows = new List<MenuRow>();
        var r = await api.GetAsync("/api/platform/v1/menu");
        if (r.IsSuccess)
        {
            foreach (var n in r.Items)
            {
                rows.Add(new MenuRow(Json.Str(n?["menuCode"]), Json.NullableStr(n?["parentMenuCode"]), Json.Str(n?["caption"]),
                    Json.NullableStr(n?["formClass"]), Json.NullableStr(n?["formArgument"]), Json.NullableStr(n?["shortcut"]), Json.Int(n?["sortOrder"])));
            }
        }
        if (rows.Count == 0) rows = BuiltInMenu();
        Menu = BuildTree(rows);
        Changed?.Invoke();
    }

    private static List<MenuNode> BuildTree(List<MenuRow> rows)
    {
        List<MenuNode> Children(string? parent) => rows
            .Select((row, index) => (row, index))
            .Where(x => parent is null ? string.IsNullOrEmpty(x.row.ParentMenuCode) : string.Equals(x.row.ParentMenuCode, parent, StringComparison.Ordinal))
            .OrderBy(x => x.row.SortOrder).ThenBy(x => x.index)
            .Select(x =>
            {
                var node = new MenuNode(x.row);
                node.Children.AddRange(Children(x.row.MenuCode));
                return node;
            })
            .ToList();

        // a top-level item without children is not shown (frmMain.vb:46-50)
        return Children(null).Where(t => t.Children.Count > 0).ToList();
    }

    /// <summary>
    /// frmMain.BuiltInMenu (frmMain.vb:233-250): used when core.MenuItem returns no rows. The legacy top-level lines carry
    /// six fields and Integer.Parse(p(6)) throws; the rows are read as intended (no shortcut, sort order last).
    /// </summary>
    private static List<MenuRow> BuiltInMenu()
    {
        string[] rows =
        [
            "FILE||&File||||10", "SYSTEM||&System||||20", "RPT||&Reports||||120",
            "WINDOW||&Window||||130", "HELP||&Help||||140",
            "FILE.COMPANY|FILE|Select &Company / Site...||$COMPANY||10",
            "FILE.EXIT|FILE|E&xit||$EXIT||90",
            "SYS.MAINT|SYSTEM|File Main&tenance...|frmMaintLauncher|||10",
            "SYS.APPROVAL|SYSTEM|&Approvals Inbox...|frmApprovals|||20",
            "SYS.EXCEPTION|SYSTEM|&Exception Worklist...|frmExceptions|||30",
            "SYS.AUDIT|SYSTEM|A&udit Enquiry...|frmAudit|||40",
            "SYS.JOBS|SYSTEM|&Job Monitor...|frmJobMonitor|||50",
            "SYS.ENGINE|SYSTEM|Engine &Diagnostics...|frmEngineDiag|||60",
            "RPT.LAUNCH|RPT|&Run Report...|frmReportLauncher|||10",
            "RPT.SPOOL|RPT|&Spool File Viewer...|frmSpoolViewer|||20",
            "WIN.CASCADE|WINDOW|&Cascade||$CASCADE||10",
            "WIN.TILEH|WINDOW|Tile &Horizontally||$TILEH||20",
            "WIN.TILEV|WINDOW|Tile &Vertically||$TILEV||30",
            "WIN.CLOSEALL|WINDOW|C&lose All||$CLOSEALL||50",
            "HELP.ABOUT|HELP|&About STERP - Stefan's ERP...||$ABOUT||90",
        ];
        return rows.Select(line => line.Split('|')).Select(p => new MenuRow(
            p[0], p[1].Length == 0 ? null : p[1], p[2], p[3].Length == 0 ? null : p[3], p[4].Length == 0 ? null : p[4],
            p[5].Length == 0 ? null : p[5], int.Parse(p[6], UkFormat.Culture))).ToList();
    }

    /// <summary>frmMain.MenuItemClicked: a FormClass opens its page; a row without FormClass runs its $ command.</summary>
    public async Task MenuItemClickedAsync(MenuNode node)
    {
        CloseMenus();
        if (node.Window is { } w)
        {
            Activate(w);
            return;
        }
        var cls = (node.Row.FormClass ?? "").Trim();
        var arg = (node.Row.FormArgument ?? "").Trim();
        if (cls.Length == 0)
        {
            await RunCommandAsync(arg);
        }
        else
        {
            OpenForm(cls, arg);
        }
    }

    // ------------------------------------------------------------------------------------------ shell commands

    /// <summary>frmMain.RunCommand (frmMain.vb:111-149).</summary>
    public async Task RunCommandAsync(string command)
    {
        switch (command.ToUpperInvariant())
        {
            case "$CASCADE":
                Cascade();
                break;
            case "$TILEH":
                TileHorizontally();
                break;
            case "$TILEV":
                TileVertically();
                break;
            case "$ARRANGE":
                ArrangeIcons();
                break;
            case "$CLOSEALL":
                foreach (var w in _windows.ToList()) await CloseWindowAsync(w);
                break;
            case "$ABOUT":
                await dialogs.ShowAsync(typeof(AboutDialog));
                break;
            case "$HELP":
                await dialogs.MessageAsync(
                    options.ProductName + "\n\n" +
                    "F2 lookup      F3 / Esc close window\n" +
                    "F5 refresh     F10 confirm or post\n\n" +
                    "Printed manuals are held by the systems team; the on-line help\n" +
                    "compiler was retired in 2011.", "STERP - Help", DialogButtons.Ok, DialogIcon.Information);
                break;
            case "$PRINTSETUP":
                // D-WEB-04: the browser print dialog is the only printer-selection surface; nothing is stored
                await browser.PrintAsync();
                break;
            case "$COMPANY":
            case "$RUNAS":
                await ChangeSignOnAsync();
                break;
            case "$EXIT":
                await ExitAsync();
                break;
            default:
                SetMessage("Command " + command + " is not available.");
                break;
        }
    }

    /// <summary>
    /// frmMain.ChangeSignOn (FR-STERP-WEB-014): the sign-on page works on a copy of the context; Cancel keeps it; a real
    /// change of user, company, site or date closes every window first and is abandoned if any window refuses.
    /// </summary>
    public async Task ChangeSignOnAsync()
    {
        var before = session.Current;
        if (before is null) return;
        var result = await dialogs.ShowAsync(typeof(SignOnDialog), new Dictionary<string, object?>
        {
            [nameof(SignOnDialog.Current)] = before,
        });
        if (result is not SessionContext after)
        {
            SetMessage("Sign-on unchanged - still " + before.Title + " in " + before.CompanyCode + "/" + before.SiteCode + ".");
            return;
        }
        if (after.ContextDiffers(before))
        {
            // windows close (and ask about unsaved work) under the context they were opened in
            foreach (var w in _windows.ToList()) await CloseWindowAsync(w);
            if (_windows.Count > 0)
            {
                SetMessage("Sign-on unchanged - close the open windows first (" + _windows.Count + " still open).");
                return;
            }
        }
        session.Set(after);
        ApplyTitle();
        await LoadMenuAsync();
        SetMessage("Signed on as " + after.Title + " in " + after.CompanyCode + "/" + after.SiteCode + ".");
    }

    /// <summary>$EXIT: closes every window (each may refuse), then ends the session in this tab.</summary>
    public async Task ExitAsync()
    {
        foreach (var w in _windows.ToList()) await CloseWindowAsync(w);
        if (_windows.Count > 0) return;
        session.Set(null);
        End("You have signed off from " + options.ProductName + ". Close this browser tab, or reload the page to sign on again.");
    }

    public void SetMessage(string text)
    {
        Message = text;
        Changed?.Invoke();
    }

    /// <summary>Program.OnThreadException: STERP-E-9999 with the error text; the event is logged by STERP.Web.</summary>
    public async Task ReportUnexpectedAsync(Exception exception)
    {
        logger.LogError(exception, "Unhandled client error");
        browser.LogClientError(exception.Message, exception.ToString());
        await dialogs.MessageAsync("STERP-E-9999: An unexpected error has occurred.\n\n" + exception.Message +
                                   "\n\nThe event has been written to the STERP.Web log.", "STERP - Error", DialogButtons.Ok, DialogIcon.Error);
    }

    public bool IsDelivered(string slice) => _delivered.Contains(slice.StartsWith("STERP-", StringComparison.Ordinal) ? slice[6..] : slice);
}
