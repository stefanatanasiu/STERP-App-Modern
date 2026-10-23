using Sterp.Web.Framework.Api;
using Sterp.Web.Framework.Pages;

namespace Sterp.Web.Pages.Po.Support;

/// <summary>
/// Common base of the three purchasing pages: BaseForm.ShowResult over a facade outcome (<see cref="PoResult"/>), the
/// facade call itself (an answer without a legacy return code is the facade's exception, shown by Guard) and the legacy
/// window size.
/// </summary>
public abstract class PoPage : StPage
{
    /// <summary>Id of the shell's MDI client area (STERP.Web.Client ShellService.WorkspaceId).</summary>
    private const string WorkspaceId = "st-workspace";

    /// <summary>
    /// CobolGateway.Link through the API: the COBOL outcome, or - for an answer that carries no legacy return code (the
    /// facade threw, a pipeline refusal, a network failure) - an <see cref="ApiFailure"/> that <c>GuardAsync</c> presents
    /// as BaseForm.Guard did.
    /// </summary>
    protected static async Task<PoResult> LinkAsync(Task<ApiResponse> call)
    {
        var r = await call;
        if (PoResult.IsException(r)) throw new ApiFailure(r);
        return PoResult.From(r);
    }

    /// <summary>BaseForm.ShowResult (BaseForm.vb:202-219): true when work may continue (RC 0 and 4).</summary>
    protected async Task<bool> ShowResultAsync(PoResult r)
    {
        switch (r.ReturnCode)
        {
            case 0:
                SetStatus(r.Message.Trim().Length > 0 ? r.Message : "Completed.", CueOk);
                return true;
            case 4:
                await ShowWarningAsync(r.Message, r.ReasonCode);
                return true;
            case 6:
                await ShowHoldAsync(r.Message, r.ReasonCode);
                return false;
            default:
                await ShowErrorAsync(r.Message, r.ReasonCode);
                return false;
        }
    }

    /// <summary>
    /// A document number as a path segment. The legacy facade passed a blank number to the program, which answered for
    /// it; a blank segment cannot be routed, so a single space is sent (the API trims it back to blank).
    /// </summary>
    protected static string Seg(string? value) => Query.Path(string.IsNullOrEmpty(value) ? " " : value);

    /// <summary>The legacy ClientSize (plus the window frame) kept inside the workspace, as BaseForm.FitToWorkspace did.</summary>
    protected void LegacySize(int clientWidth, int clientHeight)
    {
        var (aw, ah) = Browser.ElementSize(WorkspaceId);
        if (aw <= 0 || ah <= 0) return;
        Window.Width = Math.Max(Math.Min(clientWidth + 8, aw - 4), 320);
        Window.Height = Math.Max(Math.Min(clientHeight + 26, ah - 4), 220);
        Window.X = Math.Max(0, Math.Min(Window.X, aw - Window.Width));
        Window.Y = Math.Max(0, Math.Min(Window.Y, ah - Window.Height));
        Window.NotifyChanged();
    }
}
