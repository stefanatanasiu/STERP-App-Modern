using Microsoft.AspNetCore.Components;

namespace Sterp.Web.Client.Shell;

/// <summary>Shell parts (menu bar, toolbar, status bar, workspace) re-render whenever the shell state changes.</summary>
public abstract class ShellComponentBase : ComponentBase, IDisposable
{
    [Inject] protected ShellService Shell { get; set; } = default!;

    protected override void OnInitialized() => Shell.Changed += OnShellChanged;

    private void OnShellChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Shell.Changed -= OnShellChanged;
        GC.SuppressFinalize(this);
    }
}
