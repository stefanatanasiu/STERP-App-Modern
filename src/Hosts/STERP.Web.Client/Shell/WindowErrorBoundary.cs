using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Sterp.Web.Framework.Shell;

namespace Sterp.Web.Client.Shell;

/// <summary>
/// Unexpected errors of a page: while the page opens they close the window with STERP-E-0005 (frmMain.vb:103-106); later
/// they show STERP-E-9999 (Program.OnThreadException) and the page content is replaced by the error text.
/// </summary>
public sealed class WindowErrorBoundary : ErrorBoundary
{
    [Inject] private ShellService Shell { get; set; } = default!;

    [Parameter, EditorRequired] public WindowContext Window { get; set; } = default!;

    protected override async Task OnErrorAsync(Exception exception)
    {
        if (!Window.Opened)
        {
            await Shell.OpenFailedAsync(Window, exception);
            return;
        }
        Window.SetStatus(exception.Message, "*** ERROR");
        await Shell.ReportUnexpectedAsync(exception);
    }

    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        if (CurrentException is null)
        {
            builder.AddContent(0, ChildContent);
            return;
        }
        builder.OpenElement(1, "div");
        builder.AddAttribute(2, "class", "st-placeholder st-sunken");
        builder.AddContent(3, "STERP-E-9999: An unexpected error has occurred. " + CurrentException.Message);
        builder.CloseElement();
    }
}
