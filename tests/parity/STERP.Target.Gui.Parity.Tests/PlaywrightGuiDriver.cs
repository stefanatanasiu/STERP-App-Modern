using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using STERP.Testing;

namespace STERP.Target.Gui.Parity;

/// <summary>
/// Executes GUI steps of the parity DSL in the target STERP-WEB shell with Playwright on the installed
/// Microsoft Edge channel (no browser download). Binds only to routes (STERP-WEB section 7.2), legacy
/// captions, accessible names/labels and function keys (contracts 9.1-9.6), and to the binding OpenAPI
/// operations when a step stubs a response (<c>respond</c>).
/// </summary>
/// <remarks>
/// Generic rules (Agent 07 amendment agent-07-20261021T211031Z-a544e35d, pass p2; documented in tests/README.md
/// and the DSL documentation):
/// <list type="bullet">
/// <item>Acting user (RG-102). The browser carries the Windows identity, so the acting user of a GUI step is the
/// STERP user that identity is mapped to (contracts 5.1): <c>step.as</c>, else <c>context.actor</c>, else
/// <c>context.login</c> (explicit per-step mapping). After the step the scenario mapping (<c>context.login</c>)
/// is restored, so later API steps act as authored (<c>X-STERP-Run-As</c> only from a SYSADM login).
/// Legacy <i>Continue as User</i> (frmLogin.vb:46, SYSADM only) is reached in a step whose acting user is SYSADM.</item>
/// <item>Business date (RG-104). With <c>context.businessDate</c> the browser clock starts at that date (local noon) and
/// keeps flowing, as the legacy sign-on defaults the business date to Date.Today (frmLogin.vb:31).</item>
/// <item>Labels (RG-101). <c>fill</c>/<c>select</c> match the accessible label exactly (case-sensitive, whole
/// string); only when no exact match exists does a case-insensitive substring match apply, and it must be
/// unique. Several exact matches (the same caption in two windows) resolve to the last visible one (the window
/// opened most recently).</item>
/// <item>Keys (RG-103). DSL key names are translated to Playwright names (Esc=Escape, Ctrl=Control, Del=Delete,
/// Ins=Insert, PgUp=PageUp, PgDn=PageDown, Return=Enter, Up/Down/Left/Right=Arrow...).</item>
/// <item>Page text (RG-109). <c>text</c> is the visible body text followed by the displayed value of every
/// visible input, select and text area (legacy screens show these values in their boxes).</item>
/// <item>Shown texts (RG-106). <c>shown</c> lists, in order, every text the page wrote while the step ran (texts
/// of changed or replaced elements, including the replaced value), so a status message that a later list reload
/// overwrites (ListForm.Populate) is still assertable: <c>{"on": s, "json": "$.shown[*]", "contains": t}</c>.</item>
/// <item>Stubbed responses (RG-108). An action <c>{"respond": {"operation", "status", "body"}}</c> answers the
/// browser's calls of that binding operation (method and path from the OpenAPI document) with the given status
/// and body for the whole step; it is registered before the route opens.</item>
/// </list>
/// </remarks>
public sealed class PlaywrightGuiDriver : IGuiDriver
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    /// <summary>Records every text written to the document (RG-106); read back as <c>shown</c>.</summary>
    private const string ShownRecorder = """
        (() => {
          if (window.__sterpShown) return;
          const log = [];
          window.__sterpShown = log;
          const push = (t) => {
            if (t === null || t === undefined) return;
            t = String(t).trim();
            if (!t) return;
            if (t.length > 4000) t = t.substring(0, 4000);
            if (log.length > 0 && log[log.length - 1] === t) return;
            if (log.length < 20000) log.push(t);
          };
          // texts of small elements only (a status line, a message, a cell); large containers are left to the final page text
          const small = (n) => n && (n.nodeType === 3 || (n.childNodes && n.childNodes.length <= 50));
          new MutationObserver((records) => {
            const done = new Set();
            const element = (e) => {
              if (!e || done.has(e)) return;
              done.add(e);
              if (small(e)) push(e.textContent);
            };
            for (const r of records) {
              if (r.type === 'characterData') {
                push(r.oldValue);
                element(r.target.parentElement);
              } else {
                for (const n of r.removedNodes) if (small(n)) push(n.textContent);
                element(r.target);
              }
            }
          }).observe(document, { subtree: true, childList: true, characterData: true, characterDataOldValue: true });
        })();
        """;

    /// <summary>Visible text plus the displayed values of visible form controls (RG-109).</summary>
    private const string PageText = """
        () => {
          const parts = [document.body ? document.body.innerText : ''];
          const skip = new Set(['hidden', 'password', 'checkbox', 'radio', 'button', 'submit', 'reset', 'image', 'file']);
          for (const e of document.querySelectorAll('input, select, textarea')) {
            if (e.tagName === 'INPUT' && skip.has((e.type || '').toLowerCase())) continue;
            const visible = typeof e.checkVisibility === 'function'
              ? e.checkVisibility({ checkVisibilityCSS: true })
              : !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
            if (!visible) continue;
            const v = e.tagName === 'SELECT' ? Array.from(e.selectedOptions).map((o) => o.text).join(', ') : e.value;
            if (v && v.trim()) parts.push(v);
          }
          return parts.join('\n');
        }
        """;

    public async Task<JsonObject> RunAsync(ParityScenario scenario, JsonObject step, JsonObject context)
    {
        var login = Text(context, "login") ?? "SYSADM";
        var acting = Text(step, "as") ?? Text(context, "actor") ?? login;
        await IsolatedDatabase.MapLoginAsync(acting);
        if (acting != "<unmapped>")
        {
            await IsolatedDatabase.SetUserDefaultsAsync(acting, Text(context, "company"), Text(context, "site"));
        }
        try
        {
            return await RunInBrowserAsync(step, context);
        }
        finally
        {
            // later steps act as authored: the scenario's own login mapping is restored (RG-102)
            await IsolatedDatabase.MapLoginAsync(login);
        }
    }

    private static async Task<JsonObject> RunInBrowserAsync(JsonObject step, JsonObject context)
    {
        var browser = await BrowserAsync();
        await using var session = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await session.NewPageAsync();
        if (BusinessDateNoon(context) is { } noon)
        {
            // the clock starts at the business date and keeps flowing (a frozen Date.now stalls pages that poll or time out)
            await page.Clock.InstallAsync(new ClockInstallOptions { TimeDate = noon });
        }
        await page.AddInitScriptAsync(ShownRecorder);
        var actions = (step["actions"] as JsonArray ?? []).Select(n => n!.AsObject()).ToList();
        foreach (var respond in actions.Where(a => a["respond"] is JsonObject))
        {
            await RespondAsync(page, respond["respond"]!.AsObject());
        }
        var route = Text(step, "gui") ?? "/";
        await page.GotoAsync(ParityEnvironment.WebBaseUrl.TrimEnd('/') + route, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        foreach (var action in actions)
        {
            if (Text(action, "press") is { } key)
            {
                await page.Keyboard.PressAsync(KeyName(key));
            }
            else if (Text(action, "click") is { } name)
            {
                await ClickAsync(page, name);
            }
            else if (action["fill"] is JsonObject fill)
            {
                await (await FieldAsync(page, Text(fill, "label")!)).FillAsync(Text(fill, "value") ?? "");
            }
            else if (action["select"] is JsonObject select)
            {
                await (await FieldAsync(page, Text(select, "label")!)).SelectOptionAsync(Text(select, "value") ?? "");
            }
            else if (action["menu"] is JsonArray path)
            {
                foreach (var caption in path) await ClickAsync(page, JsonPathLite.AsText(caption)!.Replace("&", "", StringComparison.Ordinal));
            }
            else if (action["respond"] is JsonObject)
            {
                continue; // registered before the route opened
            }
            else
            {
                throw new InvalidOperationException($"Unknown GUI action {action.ToJsonString()}");
            }
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
        var focused = await page.EvaluateAsync<string?>(
            "() => { const e = document.activeElement; if (!e) return null; return e.getAttribute('aria-label') || (e.labels && e.labels[0] && e.labels[0].innerText) || e.name || e.id || null; }");
        var shown = new JsonArray();
        foreach (var text in await page.EvaluateAsync<string[]>("() => window.__sterpShown || []")) shown.Add(text);
        return new JsonObject
        {
            ["url"] = page.Url,
            ["title"] = await page.TitleAsync(),
            ["text"] = await page.EvaluateAsync<string>(PageText),
            ["focused"] = focused,
            ["shown"] = shown,
        };
    }

    /// <summary>RG-104: the legacy sign-on takes Date.Today as the business date; the browser day is the scenario's.</summary>
    private static DateTime? BusinessDateNoon(JsonObject context)
    {
        if (Text(context, "businessDate") is not { Length: > 0 } text) return null;
        var date = DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Local);
    }

    /// <summary>RG-101: exact accessible label first; a unique case-insensitive substring match only when no exact one exists.</summary>
    private static async Task<ILocator> FieldAsync(IPage page, string label)
    {
        // wait until a field with this caption (exact or partial) exists, e.g. in a dialog that is still opening
        var loose = page.GetByLabel(label);
        await loose.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        var exact = page.GetByLabel(label, new PageGetByLabelOptions { Exact = true });
        var count = await exact.CountAsync();
        if (count == 1) return exact;
        if (count > 1)
        {
            var visible = exact.Filter(new LocatorFilterOptions { Visible = true });
            return await visible.CountAsync() > 0 ? visible.Last : exact.Last;
        }
        var n = await loose.CountAsync();
        if (n > 1)
        {
            throw new InvalidOperationException($"Label '{label}' has no exact match and {n} partial matches; use the exact legacy caption.");
        }
        return loose;
    }

    /// <summary>RG-103: DSL key names (SCENARIO-DSL.md) to Playwright key names.</summary>
    internal static string KeyName(string dsl) => string.Join("+", dsl.Split('+').Select(part => part.ToUpperInvariant() switch
    {
        "CTRL" => "Control",
        "ESC" => "Escape",
        "DEL" => "Delete",
        "INS" => "Insert",
        "PGUP" => "PageUp",
        "PGDN" => "PageDown",
        "RETURN" => "Enter",
        "UP" => "ArrowUp",
        "DOWN" => "ArrowDown",
        "LEFT" => "ArrowLeft",
        "RIGHT" => "ArrowRight",
        _ => part,
    }));

    /// <summary>RG-108: answers the browser's calls of one binding operation with an authored status and body.</summary>
    private static async Task RespondAsync(IPage page, JsonObject respond)
    {
        var op = BindingContracts.Get(Text(respond, "operation") ?? throw new InvalidOperationException("respond needs an operation"));
        var status = respond["status"]?.GetValue<int>() ?? 200;
        var body = respond["body"]?.ToJsonString() ?? "";
        var contentType = Text(respond, "contentType") ?? (status >= 400 ? "application/problem+json" : "application/json");
        var segments = Regex.Split(op.Path, @"\{[^}]+\}").Select(Regex.Escape);
        var pattern = new Regex("^" + Regex.Escape(op.BaseUrl.TrimEnd('/')) + string.Join("[^/?#]+", segments) + @"(?:[?#].*)?$",
            RegexOptions.IgnoreCase); // Playwright accepts only the i/m/s regex flags
        await page.RouteAsync(pattern, async r =>
        {
            if (!string.Equals(r.Request.Method, op.Method, StringComparison.OrdinalIgnoreCase))
            {
                await r.ContinueAsync(); // CORS preflight and other methods reach the host
                return;
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (r.Request.Headers.TryGetValue("origin", out var origin))
            {
                headers["Access-Control-Allow-Origin"] = origin;
                headers["Access-Control-Allow-Credentials"] = "true";
                headers["Vary"] = "Origin";
            }
            await r.FulfillAsync(new RouteFulfillOptions { Status = status, ContentType = contentType, Body = body, Headers = headers });
        });
    }

    private static async Task ClickAsync(IPage page, string name)
    {
        foreach (var role in new[] { AriaRole.Menuitem, AriaRole.Button, AriaRole.Link, AriaRole.Tab })
        {
            var candidate = page.GetByRole(role, new PageGetByRoleOptions { Name = name, Exact = true });
            if (await candidate.CountAsync() > 0)
            {
                await candidate.First.ClickAsync();
                return;
            }
        }
        await page.GetByText(name, new PageGetByTextOptions { Exact = true }).First.ClickAsync();
    }

    private static async Task<IBrowser> BrowserAsync()
    {
        await Gate.WaitAsync();
        try
        {
            _playwright ??= await Playwright.CreateAsync();
            _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "msedge",
                Headless = ParityEnvironment.Get("STERP_GUI_HEADED", "0") != "1",
                Args = ["--auth-server-allowlist=localhost,127.0.0.1", "--auth-negotiate-delegate-allowlist=localhost"],
            });
            return _browser;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string? Text(JsonObject? node, string name) =>
        node?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
