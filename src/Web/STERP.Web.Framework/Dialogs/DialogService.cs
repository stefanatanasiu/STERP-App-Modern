using Microsoft.AspNetCore.Components;
using Sterp.Web.Framework.Shell;

namespace Sterp.Web.Framework.Dialogs;

public enum DialogButtons
{
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel,
}

public enum DialogIcon
{
    None,
    Information,
    Warning,
    Error,
    Question,
}

/// <summary>One open modal dialog (MessageBox, InputBox, sign-on, lookup, about).</summary>
public sealed class DialogInstance
{
    private static int _sequence;

    internal DialogInstance(Type component, IDictionary<string, object?> parameters, DialogService owner)
    {
        Component = component;
        Parameters = new Dictionary<string, object?>(parameters);
        Owner = owner;
        Id = "dlg" + Interlocked.Increment(ref _sequence);
    }

    public string Id { get; }

    public Type Component { get; }

    public Dictionary<string, object?> Parameters { get; }

    internal DialogService Owner { get; }

    internal TaskCompletionSource<object?> Completion { get; } = new();

    /// <summary>Key handler of the dialog component (Enter = default button, Escape = cancel); true when handled.</summary>
    public Func<KeyInfo, bool>? KeyHandler { get; set; }

    public bool IsClosed => Completion.Task.IsCompleted;

    /// <summary>Closes the dialog with a result; the awaiting code continues within the same browser event.</summary>
    public void Close(object? result) => Owner.Close(this, result);
}

/// <summary>
/// Modal dialogs of the shell and the pages: MessageBox.Show and InputBox equivalents with the legacy titles
/// ("STERP - Error", "STERP - Warning", "STERP - Hold", "STERP - Confirm") and custom dialog components.
/// </summary>
public sealed class DialogService
{
    private readonly List<DialogInstance> _open = [];

    public IReadOnlyList<DialogInstance> Open => _open;

    public DialogInstance? Top => _open.Count > 0 ? _open[^1] : null;

    public bool HasModal => _open.Count > 0;

    public event Action? Changed;

    public Task<object?> ShowAsync(Type component, IDictionary<string, object?>? parameters = null)
    {
        var d = new DialogInstance(component, parameters ?? new Dictionary<string, object?>(), this);
        _open.Add(d);
        Changed?.Invoke();
        return d.Completion.Task;
    }

    public async Task<TResult?> ShowAsync<TComponent, TResult>(IDictionary<string, object?>? parameters = null) where TComponent : IComponent =>
        await ShowAsync(typeof(TComponent), parameters) is TResult r ? r : default;

    internal void Close(DialogInstance dialog, object? result)
    {
        if (!_open.Remove(dialog)) return;
        Changed?.Invoke();
        dialog.Completion.TrySetResult(result);
    }

    /// <summary>MessageBox.Show: returns the caption of the button pressed ("OK", "Yes", "No", "Cancel").</summary>
    public async Task<string> MessageAsync(string text, string title, DialogButtons buttons = DialogButtons.Ok, DialogIcon icon = DialogIcon.None)
    {
        var result = await ShowAsync(typeof(MessageBoxDialog), new Dictionary<string, object?>
        {
            [nameof(MessageBoxDialog.Text)] = text,
            [nameof(MessageBoxDialog.Title)] = title,
            [nameof(MessageBoxDialog.Buttons)] = buttons,
            [nameof(MessageBoxDialog.Icon)] = icon,
        });
        return result as string ?? (buttons == DialogButtons.YesNo ? "No" : "Cancel");
    }

    /// <summary>Microsoft.VisualBasic InputBox: the text entered, or "" when cancelled (legacy semantics).</summary>
    public async Task<string> InputAsync(string prompt, string title, string defaultValue = "")
    {
        var result = await ShowAsync(typeof(InputBoxDialog), new Dictionary<string, object?>
        {
            [nameof(InputBoxDialog.Prompt)] = prompt,
            [nameof(InputBoxDialog.Title)] = title,
            [nameof(InputBoxDialog.DefaultValue)] = defaultValue,
        });
        return result as string ?? "";
    }

    /// <summary>frmLookup.Pick: the code chosen in the lookup dialog, or "" when cancelled (FR-STERP-PLATFORM-035).</summary>
    public async Task<string> LookupAsync(string source, string code, string caption, string argument = "")
    {
        var result = await ShowAsync(typeof(LookupDialog), new Dictionary<string, object?>
        {
            [nameof(LookupDialog.Source)] = source,
            [nameof(LookupDialog.Code)] = code,
            [nameof(LookupDialog.Caption)] = caption,
            [nameof(LookupDialog.Argument)] = argument,
        });
        return result as string ?? "";
    }

    /// <summary>Routes a key to the top-most dialog; dialogs are modal, so every key stops here.</summary>
    public bool HandleKey(KeyInfo key)
    {
        var top = Top;
        if (top is null) return false;
        return top.KeyHandler?.Invoke(key) ?? false;
    }
}
