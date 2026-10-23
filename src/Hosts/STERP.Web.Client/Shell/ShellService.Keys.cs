using Microsoft.JSInterop;
using Sterp.Web.Framework.Shell;

namespace Sterp.Web.Client.Shell;

public sealed partial class ShellService
{
    /// <summary>Menu codes of the open drop-downs, outermost first (empty = menu bar closed).</summary>
    public List<string> OpenPath { get; } = [];

    /// <summary>Highlighted entry of the innermost open drop-down (keyboard navigation).</summary>
    public string? HotCode { get; private set; }

    public bool IsOpen(MenuNode node) => OpenPath.Contains(node.Code);

    public void OpenTop(MenuNode top)
    {
        OpenPath.Clear();
        OpenPath.Add(top.Code);
        HotCode = null;
        Changed?.Invoke();
    }

    public void ToggleTop(MenuNode top)
    {
        if (OpenPath.Count > 0 && OpenPath[0] == top.Code) CloseMenus();
        else OpenTop(top);
    }

    /// <summary>Hovering another top-level item while a menu is open switches to it (classic menu behaviour).</summary>
    public void HoverTop(MenuNode top)
    {
        if (OpenPath.Count > 0 && OpenPath[0] != top.Code) OpenTop(top);
    }

    /// <summary>Opens the submenu of an entry that has children (keeps its ancestors open).</summary>
    public void OpenSub(MenuNode node, int depth)
    {
        while (OpenPath.Count > depth) OpenPath.RemoveAt(OpenPath.Count - 1);
        OpenPath.Add(node.Code);
        HotCode = null;
        Changed?.Invoke();
    }

    public void CloseMenus()
    {
        if (OpenPath.Count == 0 && HotCode is null) return;
        OpenPath.Clear();
        HotCode = null;
        Changed?.Invoke();
    }

    /// <summary>Entries of a drop-down; the WINDOW menu lists the open windows (MdiWindowListItem).</summary>
    public IReadOnlyList<MenuNode> ItemsOf(MenuNode node)
    {
        if (!string.Equals(node.Code, "WINDOW", StringComparison.Ordinal) || _windows.Count == 0) return node.Children;
        var list = new List<MenuNode>(node.Children);
        var i = 1;
        foreach (var w in _windows)
        {
            list.Add(new MenuNode(new MenuRow("$WINLIST." + w.Id, node.Code, "&" + i + " " + w.Title.Replace("&", "&&", StringComparison.Ordinal), null, null, null, 1000 + i))
            {
                Window = w,
            });
            i++;
        }
        return list;
    }

    private MenuNode? FindNode(string code, IEnumerable<MenuNode>? nodes = null)
    {
        foreach (var n in nodes ?? Menu)
        {
            if (n.Code == code) return n;
            var hit = FindNode(code, n.Children);
            if (hit is not null) return hit;
        }
        return null;
    }

    private IEnumerable<MenuNode> AllItems(IEnumerable<MenuNode>? nodes = null)
    {
        foreach (var n in nodes ?? Menu)
        {
            yield return n;
            foreach (var c in AllItems(n.Children)) yield return c;
        }
    }

    // ------------------------------------------------------------------------------------------- keyboard

    /// <summary>
    /// Key routing (contracts 9.4): a modal dialog takes every key; an open menu takes the navigation keys; Alt+letter opens
    /// the menu with that access key; the active window's page takes its keys (BaseForm.OnKeyDown); otherwise the menu
    /// shortcuts of core.MenuItem.Shortcut apply. Returns true when the key was used (the browser default is suppressed).
    /// </summary>
    public bool HandleKey(KeyInfo key)
    {
        if (dialogs.HasModal) return dialogs.HandleKey(key) || key.Key.StartsWith('F');
        if (Phase != ShellPhase.Ready) return false;
        if (OpenPath.Count > 0 && MenuKey(key)) return true;
        if (key.Alt && !key.Ctrl && key.Key.Length == 1)
        {
            var top = Menu.FirstOrDefault(t => t.AccessKey == char.ToUpperInvariant(key.Key[0]));
            if (top is not null)
            {
                OpenTop(top);
                return true;
            }
        }
        if (ActiveWindow is { Page: { } page } && page.HandleKey(key)) return true;
        var chord = key.Chord;
        var shortcut = AllItems().FirstOrDefault(n => n.Shortcut.Length > 0 && string.Equals(n.Shortcut.Replace(" ", "", StringComparison.Ordinal), chord, StringComparison.OrdinalIgnoreCase));
        if (shortcut is not null)
        {
            _ = Observe(MenuItemClickedAsync(shortcut));
            return true;
        }
        return false;
    }

    private bool MenuKey(KeyInfo key)
    {
        var depth = OpenPath.Count;
        var current = FindNode(OpenPath[^1]);
        if (current is null)
        {
            CloseMenus();
            return false;
        }
        var items = ItemsOf(current);
        var index = HotCode is null ? -1 : items.ToList().FindIndex(i => i.Code == HotCode);
        switch (key.Key)
        {
            case "Escape":
                if (depth > 1)
                {
                    HotCode = OpenPath[^1];
                    OpenPath.RemoveAt(OpenPath.Count - 1);
                    Changed?.Invoke();
                }
                else
                {
                    CloseMenus();
                }
                return true;
            case "ArrowDown":
                HotCode = items.Count == 0 ? null : items[(index + 1) % items.Count].Code;
                Changed?.Invoke();
                return true;
            case "ArrowUp":
                HotCode = items.Count == 0 ? null : items[(index - 1 + items.Count) % items.Count].Code;
                Changed?.Invoke();
                return true;
            case "ArrowRight":
                if (index >= 0 && items[index].Children.Count > 0)
                {
                    OpenSub(items[index], depth);
                }
                else
                {
                    var pos = Menu.FindIndex(t => t.Code == OpenPath[0]);
                    OpenTop(Menu[(pos + 1) % Menu.Count]);
                }
                return true;
            case "ArrowLeft":
                if (depth > 1)
                {
                    HotCode = OpenPath[^1];
                    OpenPath.RemoveAt(OpenPath.Count - 1);
                    Changed?.Invoke();
                }
                else
                {
                    var pos = Menu.FindIndex(t => t.Code == OpenPath[0]);
                    OpenTop(Menu[(pos - 1 + Menu.Count) % Menu.Count]);
                }
                return true;
            case "Enter":
                if (index >= 0)
                {
                    var item = items[index];
                    if (item.Children.Count > 0) OpenSub(item, depth);
                    else _ = Observe(MenuItemClickedAsync(item));
                }
                return true;
        }
        if (key.Key.Length == 1 && !key.Ctrl)
        {
            var letter = char.ToUpperInvariant(key.Key[0]);
            if (key.Alt)
            {
                var top = Menu.FirstOrDefault(t => t.AccessKey == letter);
                if (top is not null)
                {
                    OpenTop(top);
                    return true;
                }
                return false;
            }
            var hit = items.FirstOrDefault(i => i.AccessKey == letter);
            if (hit is not null)
            {
                if (hit.Children.Count > 0) OpenSub(hit, depth);
                else _ = Observe(MenuItemClickedAsync(hit));
                return true;
            }
        }
        return false;
    }

    /// <summary>Runs shell work started by a key or a click; an unexpected exception shows STERP-E-9999.</summary>
    public async Task Observe(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception ex)
        {
            await ReportUnexpectedAsync(ex);
        }
    }
}

/// <summary>Receives the keydown events captured by sterp.js (synchronous JS interop) and routes them to the shell.</summary>
public sealed class KeyboardRouter(ShellService shell)
{
    [JSInvokable]
    public bool OnKeyDown(string key, bool ctrl, bool alt, bool shift, string tag, string id, string field, string type, string windowId, bool caps, bool num)
    {
        if (shell.CapsLock != caps || shell.NumLock != num)
        {
            shell.CapsLock = caps;
            shell.NumLock = num;
            shell.NotifyChanged();
        }
        try
        {
            return shell.HandleKey(new KeyInfo(key, ctrl, alt, shift, tag, id, field, type));
        }
        catch (Exception ex)
        {
            _ = shell.ReportUnexpectedAsync(ex);
            return true;
        }
    }

    [JSInvokable]
    public void OnLockKeys(bool caps, bool num)
    {
        shell.CapsLock = caps;
        shell.NumLock = num;
        shell.NotifyChanged();
    }
}
