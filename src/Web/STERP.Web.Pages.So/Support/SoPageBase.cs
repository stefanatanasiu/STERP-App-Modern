using Sterp.Web.Framework.Pages;

namespace Sterp.Web.Pages.So.Support;

/// <summary>
/// The BaseForm members the Sales forms call with a CobolResult (BaseForm.vb:202-219): ShowResult and the company base
/// currency of the session (SterpSession.BaseCurrency).
/// </summary>
public abstract class SoPageBase : StPage
{
    /// <summary>SterpSession.Current.BaseCurrency: the base currency of the signed-on company.</summary>
    protected string BaseCurrency => Session.Selected?.BaseCurrency ?? "";

    /// <summary>
    /// BaseForm.ShowResult: RC 0 the message (or "Completed.") with cue OK; RC 4 the warning dialog; RC 6 the hold dialog;
    /// anything else the error dialog, each with the reason code. True when work may continue (RC 0 and 4).
    /// </summary>
    protected async Task<bool> ShowResultAsync(SoResult r)
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
}
