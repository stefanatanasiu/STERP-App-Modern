using Microsoft.AspNetCore.Components;
using Sterp.Web.Framework.Api;
using Sterp.Web.Framework.Dialogs;
using Sterp.Web.Framework.Format;
using Sterp.Web.Framework.Session;
using Sterp.Web.Framework.Shell;

namespace Sterp.Web.Framework.Pages;

/// <summary>
/// Base of every STERP window page - the legacy <c>BaseForm</c> (BaseForm.vb, FR-STERP-WEB-015): function keys F1 help,
/// F2 lookup, F3/Esc close, F5 refresh, F10 confirm/post; a status line with text cues (OK, !  WARNING, ## HOLD,
/// *** ERROR, BUSY); outcome presentation with "Reason code: &lt;code&gt;" (contracts 9.3); a close guard for unsaved work.
/// </summary>
public abstract class StPage : ComponentBase, IWindowPage, IDisposable
{
    public const string CueOk = "OK";
    public const string CueWarning = "!  WARNING";
    public const string CueHold = "## HOLD";
    public const string CueError = "*** ERROR";
    public const string CueBusy = "BUSY";

    /// <summary>BaseForm.ShowHold text (BaseForm.vb:189-195).</summary>
    public const string HoldText = "The document has been placed on hold and an approval has been requested.";

    private string _restCue = "";
    private string _restText = "";

    [CascadingParameter] public WindowContext Window { get; set; } = default!;

    [Inject] protected ApiClient Api { get; set; } = default!;

    [Inject] protected SessionService SessionService { get; set; } = default!;

    [Inject] protected DialogService Dialogs { get; set; } = default!;

    [Inject] protected IShell Shell { get; set; } = default!;

    [Inject] protected BrowserInterop Browser { get; set; } = default!;

    /// <summary>The signed-on context (company, site, business date, user).</summary>
    protected SessionContext Session => SessionService.Current!;

    /// <summary>core.MenuItem.FormArgument of the entry that opened this page.</summary>
    protected string FormArgument => Window?.FormArgument ?? "";

    protected string ProductName => Shell.Options.ProductName;

    /// <summary>Element id prefix unique to this window (labels and fields).</summary>
    protected string Uid(string name) => $"{Window.Id}-{name}";

    protected override void OnInitialized()
    {
        Window.Attach(this);
    }

    public virtual void Dispose()
    {
        Window?.Detach(this);
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------------------------------------ function keys

    /// <summary>BaseForm.OnKeyDown (KeyPreview): page keys first (<see cref="OnKey"/>), then F1, F2, F3, Esc (without Shift), F5, F10.</summary>
    public bool HandleKey(KeyInfo key)
    {
        if (OnKey(key) is { } pageWork)
        {
            Run(pageWork);
            return true;
        }
        if (key.Ctrl || key.Alt) return false;
        Task? work = key.Key switch
        {
            "F1" => OnHelpKeyAsync(),
            "F2" => OnLookupAsync(key),
            "F3" => CloseAsync(),
            "Escape" when !key.Shift => CloseAsync(),
            "F5" => OnRefreshKeyAsync(),
            "F10" => OnConfirmAsync(),
            _ => null,
        };
        if (work is null) return false;
        Run(work);
        return true;
    }

    /// <summary>
    /// Page-specific keys (Ctrl+PgDn, Enter in a search box ...), checked before the standard keys: return the work to run
    /// when the page takes the key, null otherwise.
    /// </summary>
    protected virtual Task? OnKey(KeyInfo key) => null;

    /// <summary>Runs page work started by a key or a timer; an unexpected exception shows STERP-E-9999.</summary>
    protected async void Run(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception ex)
        {
            await Shell.ReportUnexpectedAsync(ex);
        }
    }

    /// <summary>F1 - module help (BaseForm.OnHelpKey).</summary>
    protected virtual Task OnHelpKeyAsync() =>
        InfoAsync(ProductName + "\n\nScreen: " + Window.Title + "\nF2 lookup   F3/Esc close   F5 refresh   F10 confirm\n\n" +
                  "Printed documentation is held by the systems team.", "Help");

    /// <summary>F2 - lookup for the field with the focus; default "No lookup is available for this field.".</summary>
    protected virtual Task OnLookupAsync(KeyInfo key)
    {
        SetStatus("No lookup is available for this field.");
        return Task.CompletedTask;
    }

    /// <summary>F5 - re-read the data.</summary>
    protected virtual Task OnRefreshKeyAsync() => Task.CompletedTask;

    /// <summary>F10 - confirm / post the work in progress.</summary>
    protected virtual Task OnConfirmAsync() => Task.CompletedTask;

    /// <summary>BaseForm.CanClose: data entry pages ask about unsaved changes.</summary>
    public virtual Task<bool> CanCloseAsync() => Task.FromResult(true);

    /// <summary>BaseForm.CloseForm: closes the window through the shell (the close guard runs first).</summary>
    protected Task<bool> CloseAsync() => Shell.CloseWindowAsync(Window);

    // ----------------------------------------------------------------------------------------- status and busy

    /// <summary>BaseForm.SetStatus: status line text with a text cue (COM-024).</summary>
    protected void SetStatus(string text, string cue = "") => Window.SetStatus(text, cue);

    /// <summary>BaseForm.Busy: "BUSY / Working..." while a service call runs; the previous line comes back afterwards.</summary>
    protected void Busy(bool busy)
    {
        if (busy)
        {
            if (Window.StatusCue != CueBusy)
            {
                _restCue = Window.StatusCue;
                _restText = Window.StatusText;
            }
            SetStatus("Working...", CueBusy);
        }
        else
        {
            SetStatus("");
        }
    }

    private void ClearBusy()
    {
        if (Window.StatusCue == CueBusy) SetStatus(_restText, _restCue);
    }

    // ------------------------------------------------------------------------------------------- message boxes

    protected Task InfoAsync(string text, string title) =>
        Dialogs.MessageAsync(text, "STERP - " + title, DialogButtons.Ok, DialogIcon.Information);

    /// <summary>BaseForm.ShowError: cue *** ERROR and an error dialog with "Reason code: &lt;code&gt;".</summary>
    protected Task ShowErrorAsync(string text, string reasonCode = "")
    {
        SetStatus(text, CueError);
        return Dialogs.MessageAsync(Tagged(text, reasonCode), "STERP - Error", DialogButtons.Ok, DialogIcon.Error);
    }

    /// <summary>BaseForm.ShowWarning: cue !  WARNING and a warning dialog with the reason code.</summary>
    protected Task ShowWarningAsync(string text, string reasonCode)
    {
        SetStatus(text, CueWarning);
        return Dialogs.MessageAsync(Tagged(text, reasonCode), "STERP - Warning", DialogButtons.Ok, DialogIcon.Warning);
    }

    /// <summary>BaseForm.ShowHold (RC 06): the instruction needs an authority the user does not hold.</summary>
    protected Task ShowHoldAsync(string text, string reasonCode)
    {
        SetStatus(text, CueHold);
        return Dialogs.MessageAsync(Tagged(text, reasonCode) + "\n\n" + HoldText, "STERP - Hold", DialogButtons.Ok, DialogIcon.Warning);
    }

    /// <summary>BaseForm.Confirm: Yes/No question titled "STERP - Confirm".</summary>
    protected async Task<bool> ConfirmAsync(string text) =>
        await Dialogs.MessageAsync(text, "STERP - Confirm", DialogButtons.YesNo, DialogIcon.Question) == "Yes";

    /// <summary>VB InputBox: the text entered, or "" when cancelled.</summary>
    protected Task<string> InputBoxAsync(string prompt, string title, string defaultValue = "") =>
        Dialogs.InputAsync(prompt, title, defaultValue);

    /// <summary>BaseForm.Tagged: text + blank line + "Reason code: &lt;code&gt;" when a code is present.</summary>
    public static string Tagged(string text, string? reasonCode) =>
        string.IsNullOrWhiteSpace(reasonCode) ? text : text + "\n\nReason code: " + reasonCode.Trim();

    // ----------------------------------------------------------------------------------- outcome presentation

    /// <summary>
    /// BaseForm.ShowResult for an API response (contracts 9.3): RC 0 status message (or "Completed.") with cue OK; RC 4 warning
    /// dialog; RC 6 hold dialog; RC &gt;= 8 and problems error dialog with the reason code (SYS0001 when the problem has none).
    /// Returns true when work may continue (RC 0 and 4).
    /// </summary>
    protected async Task<bool> ShowResultAsync(ApiResponse response)
    {
        if (response.IsSuccess)
        {
            var o = response.Outcome;
            if (o is null)
            {
                SetStatus("Completed.", CueOk);
                return true;
            }
            switch (o.ReturnCode)
            {
                case 0:
                    SetStatus(o.Message.Trim().Length > 0 ? o.Message : "Completed.", CueOk);
                    return true;
                case 4:
                    await ShowWarningAsync(o.Message, o.ReasonCode);
                    return true;
                case 6:
                    await ShowHoldAsync(o.Message, o.ReasonCode);
                    return false;
                default:
                    await ShowErrorAsync(o.Message, o.ReasonCode);
                    return false;
            }
        }
        await ShowProblemAsync(response);
        return false;
    }

    /// <summary>Error presentation of a failed call (BaseForm.Guard): the legacy message and reason code, SYS0001 when absent.</summary>
    protected Task ShowProblemAsync(ApiResponse response)
    {
        var p = response.Problem!;
        if (p.ReturnCode == 6) return ShowHoldAsync(p.Detail, p.ReasonCode);
        if (p.ReturnCode == 4) return ShowWarningAsync(p.Detail, p.ReasonCode);
        return ShowErrorAsync(p.Detail, p.ReasonCode.Length > 0 ? p.ReasonCode : "SYS0001");
    }

    /// <summary>
    /// Services that returned a business refusal as text (MaintService.Save, ApprovalService.Decide, ExceptionService.Resolve):
    /// a business-rule problem shows "&lt;text&gt;  (&lt;code&gt;)" without a reason code line; anything else as Guard showed it.
    /// </summary>
    protected Task ShowRefusalAsync(ApiResponse response)
    {
        var p = response.Problem!;
        if (p.Kind == "business-rule" && p.ReasonCode.Length > 0) return ShowErrorAsync(p.Detail + "  (" + p.ReasonCode + ")", "");
        return ShowProblemAsync(response);
    }

    /// <summary>
    /// BaseForm.Guard: runs a service call with the BUSY cue; an unexpected exception becomes an error dialog with SYS0001.
    /// Returns true when the work completed without an exception.
    /// </summary>
    protected async Task<bool> GuardAsync(Func<Task> work)
    {
        try
        {
            Busy(true);
            await work();
            return true;
        }
        catch (ApiFailure failure)
        {
            ClearBusy();
            await ShowProblemAsync(failure.Response);
            return false;
        }
        catch (Exception ex)
        {
            Browser.LogClientError($"{Window.FormClass}: {ex.Message}", ex.ToString());
            await ShowErrorAsync(ex.Message, "SYS0001");
            return false;
        }
        finally
        {
            ClearBusy();
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Calls an operation inside <see cref="GuardAsync"/>; a failed response is shown and null is returned.</summary>
    protected async Task<ApiResponse?> CallAsync(Func<Task<ApiResponse>> call)
    {
        ApiResponse? result = null;
        await GuardAsync(async () =>
        {
            var r = await call();
            if (!r.IsSuccess)
            {
                ClearBusy();
                await ShowProblemAsync(r);
                return;
            }
            result = r;
        });
        return result;
    }

    // ----------------------------------------------------------------------------------------------- helpers

    /// <summary>Opens (or re-activates) another page, as frmMain.OpenForm.</summary>
    protected void OpenForm(string formClass, string? argument = null) => Shell.OpenForm(formClass, argument);

    protected static string Date(DateOnly d) => UkFormat.Date(d);

    protected Task FocusFirstAsync() => Browser.FocusFirstAsync(Window.BodyId).AsTask();
}
