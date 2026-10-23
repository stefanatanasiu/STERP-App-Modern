namespace Sterp.Web.Framework.Shell;

/// <summary>A key press routed by the shell (keydown in the browser, KeyPreview semantics of BaseForm).</summary>
public sealed record KeyInfo(string Key, bool Ctrl, bool Alt, bool Shift, string TargetTag, string TargetId, string TargetField, string TargetType)
{
    public bool Plain => !Ctrl && !Alt && !Shift;

    public bool Is(string key) => string.Equals(Key, key, StringComparison.OrdinalIgnoreCase) && !Ctrl && !Alt;

    /// <summary>True when the key was pressed in a text entry control (input, select, textarea).</summary>
    public bool InTextEntry => TargetTag is "input" or "textarea" or "select";

    /// <summary>"Ctrl+K", "Alt+F4", "F8" - the notation of core.MenuItem.Shortcut.</summary>
    public string Chord => (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + Key;
}

/// <summary>Window state of an MDI-like child window.</summary>
public enum WindowState
{
    Normal,
    Minimized,
    Maximized,
}

/// <summary>
/// One child window of the workspace (an MDI child of frmMain). Pages receive it as a cascading parameter; it carries the
/// legacy FormClass/FormArgument identity, the caption, the status line (cue + text) and the geometry.
/// </summary>
public sealed class WindowContext
{
    private static int _sequence;

    public WindowContext(string formClass, string formArgument, string title, int width, int height)
    {
        Id = "w" + Interlocked.Increment(ref _sequence);
        FormClass = formClass;
        FormArgument = formArgument;
        Title = title;
        Width = width;
        Height = height;
    }

    public string Id { get; }

    public string FormClass { get; }

    /// <summary>core.MenuItem.FormArgument of the entry that opened the window (BaseForm.FormArgument).</summary>
    public string FormArgument { get; }

    /// <summary>Values a caller placed on the new form before showing it (frmReportLauncher.InitialParameters).</summary>
    public IReadOnlyDictionary<string, string> InitialParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Title { get; private set; }

    public string StatusText { get; private set; } = "";

    public string StatusCue { get; private set; } = "";

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int Z { get; set; }

    public WindowState State { get; set; } = WindowState.Normal;

    /// <summary>Restore bounds while minimized or maximized.</summary>
    public (int X, int Y, int W, int H)? RestoreBounds { get; set; }

    public bool Closed { get; set; }

    /// <summary>True once the window has been rendered for the first time (later errors are not open failures).</summary>
    public bool Opened { get; set; }

    /// <summary>The page shown in the window (keys, close guard); set by the page itself.</summary>
    public IWindowPage? Page { get; private set; }

    /// <summary>Raised when the title, status or geometry changes, so the frame re-renders.</summary>
    public event Action? Changed;

    public string BodyId => Id + "-body";

    public void Attach(IWindowPage page) => Page = page;

    public void Detach(IWindowPage page)
    {
        if (ReferenceEquals(Page, page)) Page = null;
    }

    public void SetTitle(string title)
    {
        if (Title == title) return;
        Title = title;
        Changed?.Invoke();
    }

    /// <summary>BaseForm.SetStatus: the cue is text, never colour alone (COM-024).</summary>
    public void SetStatus(string text, string cue = "")
    {
        StatusText = text ?? "";
        StatusCue = cue ?? "";
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();
}

/// <summary>What the shell needs from the page shown in a window.</summary>
public interface IWindowPage
{
    /// <summary>
    /// Routes a key to the page (F1/F2/F3/Esc/F5/F10 and page keys). Answers synchronously whether the key is the page's
    /// (so the browser default can be suppressed) and runs the work asynchronously.
    /// </summary>
    bool HandleKey(KeyInfo key);

    /// <summary>BaseForm.CanClose: asks about unsaved work; false keeps the window open.</summary>
    Task<bool> CanCloseAsync();
}

/// <summary>The shell services a page may use (frmMain's public surface).</summary>
public interface IShell
{
    SterpClientOptions Options { get; }

    /// <summary>frmMain.OpenForm: opens or re-activates the page of a legacy form class with an argument.</summary>
    void OpenForm(string formClass, string? argument = null);

    /// <summary>
    /// A page creating and showing another form itself (<c>Dim f As New frmX : f.Show()</c>): always a new window, with the
    /// values the caller set on it before showing it.
    /// </summary>
    void OpenNew(string formClass, string? argument, IReadOnlyDictionary<string, string>? initialParameters = null);

    /// <summary>Runs a built-in shell command ($HELP, $CASCADE ...).</summary>
    Task RunCommandAsync(string command);

    /// <summary>frmMain.SetMessage: the message area of the status bar.</summary>
    void SetMessage(string text);

    /// <summary>Closes a window after its close guard agreed.</summary>
    Task<bool> CloseWindowAsync(WindowContext window);

    IReadOnlyList<WindowContext> Windows { get; }

    WindowContext? ActiveWindow { get; }

    /// <summary>Target application ids of the slices the API hosts deliver ("STERP-ITM").</summary>
    IReadOnlyCollection<string> DeliveredSlices { get; }

    /// <summary>Shows the STERP-E-9999 dialog for an unexpected client error and logs it.</summary>
    Task ReportUnexpectedAsync(Exception exception);
}
