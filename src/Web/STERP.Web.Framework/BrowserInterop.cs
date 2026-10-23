using Microsoft.JSInterop;

namespace Sterp.Web.Framework;

/// <summary>Client configuration served anonymously by STERP.Web (<c>/_sterp/config</c>): product name and API base URLs.</summary>
public sealed class SterpClientOptions
{
    public string ProductName { get; set; } = "STERP - Supply Chain Management";

    public string ApiBaseUrl { get; set; } = "https://localhost:7101";

    public string JobsBaseUrl { get; set; } = "https://localhost:7102";

    public string Version { get; set; } = "";
}

/// <summary>
/// JavaScript interop of the shell (sterp.js). Calls are synchronous where the activity signal must be raised inside the
/// browser event that started the work (WebAssembly in-process runtime).
/// </summary>
public sealed class BrowserInterop(IJSRuntime js)
{
    private IJSInProcessRuntime? Sync => js as IJSInProcessRuntime;

    public IJSRuntime Runtime => js;

    public void ActivityBegin() => Sync?.InvokeVoid("sterpActivity.begin");

    public void ActivityEnd() => Sync?.InvokeVoid("sterpActivity.end");

    public void ActivityPause() => Sync?.InvokeVoid("sterpActivity.pause");

    public void ActivityResume() => Sync?.InvokeVoid("sterpActivity.resume");

    public void StartupDone() => Sync?.InvokeVoid("sterpActivity.startupDone");

    /// <summary>Holds the busy signal until the returned scope is disposed (non-network work such as job polling).</summary>
    public IDisposable Busy()
    {
        ActivityBegin();
        return new Scope(this);
    }

    public ValueTask<bool> FocusFirstAsync(string containerId) => js.InvokeAsync<bool>("sterp.focusFirst", containerId);

    public ValueTask<bool> FocusAsync(string elementId) => js.InvokeAsync<bool>("sterp.focus", elementId);

    public ValueTask<bool> SelectAllAsync(string elementId) => js.InvokeAsync<bool>("sterp.selectAll", elementId);

    public bool ContainsFocus(string elementId) => Sync?.Invoke<bool>("sterp.containsFocus", elementId) ?? false;

    public ValueTask ScrollIntoViewAsync(string elementId) => js.InvokeVoidAsync("sterp.scrollIntoView", elementId);

    public ValueTask HighlightAsync(string elementId, int start, int length) => js.InvokeVoidAsync("sterp.highlight", elementId, start, length);

    public (int Width, int Height) ElementSize(string elementId)
    {
        var size = Sync?.Invoke<int[]>("sterp.elementSize", elementId) ?? [0, 0];
        return (size.Length > 0 ? size[0] : 0, size.Length > 1 ? size[1] : 0);
    }

    public void SetTitle(string text) => Sync?.InvokeVoid("sterp.setTitle", text);

    public ValueTask PrintAsync() => js.InvokeVoidAsync("sterp.print");

    public ValueTask DownloadAsync(string fileName, string text) => js.InvokeVoidAsync("sterp.download", fileName, text);

    public void ReplaceUrl(string path) => Sync?.InvokeVoid("sterp.replaceUrl", path);

    public void LogClientError(string message, string? detail) => Sync?.InvokeVoid("sterp.logClientError", message, detail ?? "");

    private sealed class Scope(BrowserInterop owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.ActivityEnd();
        }
    }
}
