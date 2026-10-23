using Sterp.Web.Framework.Dialogs;
using Sterp.Web.Framework.Registry;
using Sterp.Web.Framework.Shell;

namespace Sterp.Web.Client.Shell;

/// <summary>A window's content: the registered page, or the placeholder of an undelivered slice (contracts 9.2).</summary>
public sealed record WindowContent(Type? Component, GuiRoute? Route, bool Placeholder);

public sealed partial class ShellService
{
    private readonly List<WindowContext> _windows = [];
    private readonly Dictionary<string, WindowContent> _content = [];
    private int _z = 10;
    private int _nextOffset;

    public const string WorkspaceId = "st-workspace";

    public IReadOnlyList<WindowContext> Windows => _windows;

    public WindowContext? ActiveWindow { get; private set; }

    public WindowContent ContentOf(WindowContext w) => _content.TryGetValue(w.Id, out var c) ? c : new WindowContent(null, null, true);

    /// <summary>
    /// frmMain.OpenForm (frmMain.vb:73-108): an open window of the same class and argument is activated; an unknown class
    /// shows STERP-E-0004; a class whose slice (or page) is not delivered opens the placeholder of contracts 9.2.
    /// </summary>
    public void OpenForm(string formClass, string? argument = null)
    {
        var cls = (formClass ?? "").Trim();
        var arg = (argument ?? "").Trim();
        foreach (var open in _windows)
        {
            if (string.Equals(open.FormClass, cls, StringComparison.OrdinalIgnoreCase) && string.Equals(open.FormArgument, arg, StringComparison.Ordinal))
            {
                Activate(open);
                return;
            }
        }
        Open(cls, arg, null);
    }

    /// <summary>A form created and shown by a page (<c>New frmX ... Show()</c>): never re-activates an open instance.</summary>
    public void OpenNew(string formClass, string? argument, IReadOnlyDictionary<string, string>? initialParameters = null) =>
        Open((formClass ?? "").Trim(), (argument ?? "").Trim(), initialParameters);

    private void Open(string cls, string arg, IReadOnlyDictionary<string, string>? initialParameters)
    {
        if (string.Equals(cls, "frmAbout", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunCommandAsync("$ABOUT");
            return;
        }
        if (string.Equals(cls, "frmLogin", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunCommandAsync("$COMPANY");
            return;
        }
        var route = GuiRoutes.Find(cls);
        if (route is null)
        {
            _ = dialogs.MessageAsync("STERP-E-0004: Screen " + cls + " is not installed in this release.", "STERP - Menu",
                DialogButtons.Ok, DialogIcon.Warning);
            return;
        }
        if (string.Equals(cls, "frmLookup", StringComparison.OrdinalIgnoreCase))
        {
            _ = dialogs.LookupAsync("master", "", "");
            return;
        }
        try
        {
            var reg = registry.Find(cls);
            var delivered = reg is not null && IsDelivered(route.Slice);
            var title = route.Caption;
            var window = new WindowContext(route.FormClass, arg, title, route.Width, route.Height)
            {
                InitialParameters = new Dictionary<string, string>(initialParameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            };
            window.Changed += () =>
            {
                if (ReferenceEquals(ActiveWindow, window)) ApplyTitle();
            };
            _content[window.Id] = new WindowContent(delivered ? reg!.Component : null, route, !delivered);
            Place(window);
            _windows.Add(window);
            Activate(window);
            SetMessage(window.Title);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OpenForm {FormClass}", cls);
            _ = dialogs.MessageAsync("STERP-E-0005: " + cls + " could not be opened.\n" + ex.Message, "STERP - Menu", DialogButtons.Ok, DialogIcon.Error);
        }
    }

    /// <summary>A page that failed while opening (first render) is closed with STERP-E-0005 (frmMain.vb:103-106).</summary>
    public async Task OpenFailedAsync(WindowContext window, Exception ex)
    {
        Remove(window);
        browser.LogClientError("OpenForm " + window.FormClass + ": " + ex.Message, ex.ToString());
        await dialogs.MessageAsync("STERP-E-0005: " + window.FormClass + " could not be opened.\n" + ex.Message, "STERP - Menu", DialogButtons.Ok, DialogIcon.Error);
    }

    /// <summary>Opens the page of a deep link (STERP-WEB section 7.2 routes) after sign-on.</summary>
    private void OpenDeepLink(string path, string? query)
    {
        var match = GuiRoutes.Match(path, query);
        if (match is null) return;
        var (route, arg) = match.Value;
        if (route.FormClass == "frmLookup")
        {
            _ = dialogs.LookupAsync(GuiRoutes.QueryValue(query, "source") ?? "master", GuiRoutes.QueryValue(query, "code") ?? "",
                GuiRoutes.QueryValue(query, "caption") ?? "", GuiRoutes.QueryValue(query, "argument") ?? "");
            return;
        }
        OpenForm(route.FormClass, arg);
    }

    /// <summary>Brings a window to the front and makes it the target of the keys (Form.Activate).</summary>
    public void Activate(WindowContext window)
    {
        if (window.Closed) return;
        if (window.State == WindowState.Minimized) Restore(window);
        if (!ReferenceEquals(ActiveWindow, window) || window.Z != _z)
        {
            window.Z = ++_z;
        }
        var changed = !ReferenceEquals(ActiveWindow, window);
        ActiveWindow = window;
        if (changed)
        {
            var route = ContentOf(window).Route;
            if (route is not null) browser.ReplaceUrl(route.UrlFor(window.FormArgument));
        }
        ApplyTitle();
        Changed?.Invoke();
    }

    /// <summary>Closes a window after its close guard (BaseForm.CanClose) agreed.</summary>
    public async Task<bool> CloseWindowAsync(WindowContext window)
    {
        if (window.Closed) return true;
        if (window.Page is { } page && !await page.CanCloseAsync()) return false;
        Remove(window);
        return true;
    }

    private void Remove(WindowContext window)
    {
        window.Closed = true;
        _windows.Remove(window);
        _content.Remove(window.Id);
        if (ReferenceEquals(ActiveWindow, window))
        {
            ActiveWindow = _windows.Where(w => w.State != WindowState.Minimized).OrderByDescending(w => w.Z).FirstOrDefault()
                           ?? _windows.OrderByDescending(w => w.Z).FirstOrDefault();
            browser.ReplaceUrl(ActiveWindow is { } a && ContentOf(a).Route is { } r ? r.UrlFor(a.FormArgument) : "/");
            ApplyTitle();
        }
        if (_windows.Count == 0) _nextOffset = 0;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------------------------------- geometry

    private (int W, int H) Area()
    {
        var (w, h) = browser.ElementSize(WorkspaceId);
        return (w > 0 ? w : 1000, h > 0 ? h : 560);
    }

    /// <summary>WindowsDefaultLocation staggering plus BaseForm.FitToWorkspace (BaseForm.vb:34-52).</summary>
    private void Place(WindowContext w)
    {
        var (aw, ah) = Area();
        w.Width = Math.Max(Math.Min(w.Width, aw - 4), 320);
        w.Height = Math.Max(Math.Min(w.Height, ah - 4), 220);
        var offset = _nextOffset * 24;
        if (offset + w.Width > aw || offset + w.Height > ah)
        {
            _nextOffset = 0;
            offset = 0;
        }
        _nextOffset++;
        w.X = Math.Max(0, Math.Min(offset, aw - w.Width));
        w.Y = Math.Max(0, Math.Min(offset, ah - w.Height));
    }

    /// <summary>$CASCADE (LayoutMdi Cascade).</summary>
    public void Cascade()
    {
        var (aw, ah) = Area();
        var list = _windows.Where(w => w.State != WindowState.Minimized).OrderBy(w => w.Z).ToList();
        var i = 0;
        foreach (var w in list)
        {
            if (w.State == WindowState.Maximized) Restore(w);
            w.Width = Math.Max(320, Math.Min(w.Width, aw - 4 - i * 22));
            w.Height = Math.Max(220, Math.Min(w.Height, ah - 4 - i * 22));
            w.X = Math.Min(i * 22, Math.Max(0, aw - w.Width));
            w.Y = Math.Min(i * 22, Math.Max(0, ah - w.Height));
            w.NotifyChanged();
            i++;
        }
        _nextOffset = i;
        Changed?.Invoke();
    }

    /// <summary>$TILEH (LayoutMdi TileHorizontal): windows stacked one above the other, full width.</summary>
    public void TileHorizontally()
    {
        var (aw, ah) = Area();
        var list = _windows.Where(w => w.State != WindowState.Minimized).OrderBy(w => w.Z).ToList();
        if (list.Count == 0) return;
        var h = ah / list.Count;
        for (var i = 0; i < list.Count; i++)
        {
            var w = list[i];
            w.State = WindowState.Normal;
            w.X = 0;
            w.Y = i * h;
            w.Width = aw;
            w.Height = Math.Max(60, h);
            w.NotifyChanged();
        }
        Changed?.Invoke();
    }

    /// <summary>$TILEV (LayoutMdi TileVertical): windows side by side, full height.</summary>
    public void TileVertically()
    {
        var (aw, ah) = Area();
        var list = _windows.Where(w => w.State != WindowState.Minimized).OrderBy(w => w.Z).ToList();
        if (list.Count == 0) return;
        var width = aw / list.Count;
        for (var i = 0; i < list.Count; i++)
        {
            var w = list[i];
            w.State = WindowState.Normal;
            w.X = i * width;
            w.Y = 0;
            w.Width = Math.Max(160, width);
            w.Height = ah;
            w.NotifyChanged();
        }
        Changed?.Invoke();
    }

    /// <summary>$ARRANGE (LayoutMdi ArrangeIcons): minimized windows lined up along the bottom edge.</summary>
    public void ArrangeIcons()
    {
        var (_, ah) = Area();
        var i = 0;
        foreach (var w in _windows.Where(w => w.State == WindowState.Minimized))
        {
            w.X = i * 164;
            w.Y = ah - 24;
            w.NotifyChanged();
            i++;
        }
        Changed?.Invoke();
    }

    public void Minimize(WindowContext w)
    {
        if (w.State == WindowState.Normal) w.RestoreBounds = (w.X, w.Y, w.Width, w.Height);
        w.State = WindowState.Minimized;
        ArrangeIcons();
        if (ReferenceEquals(ActiveWindow, w))
        {
            ActiveWindow = _windows.Where(x => x.State != WindowState.Minimized).OrderByDescending(x => x.Z).FirstOrDefault() ?? w;
            ApplyTitle();
        }
        w.NotifyChanged();
        Changed?.Invoke();
    }

    public void ToggleMaximize(WindowContext w)
    {
        if (w.State == WindowState.Maximized)
        {
            Restore(w);
        }
        else
        {
            if (w.State == WindowState.Normal) w.RestoreBounds = (w.X, w.Y, w.Width, w.Height);
            var (aw, ah) = Area();
            w.State = WindowState.Maximized;
            w.X = 0;
            w.Y = 0;
            w.Width = aw;
            w.Height = ah;
        }
        Activate(w);
        w.NotifyChanged();
    }

    public void Restore(WindowContext w)
    {
        if (w.RestoreBounds is { } b)
        {
            (w.X, w.Y, w.Width, w.Height) = b;
        }
        w.State = WindowState.Normal;
        w.NotifyChanged();
    }

    /// <summary>Moves a window by a title-bar drag (kept inside the workspace).</summary>
    public void MoveBy(WindowContext w, int dx, int dy)
    {
        var (aw, ah) = Area();
        w.X = Math.Max(0, Math.Min(w.X + dx, Math.Max(0, aw - 40)));
        w.Y = Math.Max(0, Math.Min(w.Y + dy, Math.Max(0, ah - 20)));
        w.NotifyChanged();
    }
}
