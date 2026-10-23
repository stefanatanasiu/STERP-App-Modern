using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Sterp.ApiPortal.Portal;

/// <summary>
/// Server-rendered pages of the API Console: the shell (status bar and operation tree), the catalogue overview (GET /,
/// FR-STERP-PORTAL-002) and the guide (GET /guide, FR-STERP-PORTAL-013). Document and operation pages, Try it out, the
/// contract-drift check and the Try it out settings are rendered in the browser by console.js.
/// </summary>
public static class ConsolePages
{
    public const string ConsoleTitle = "STERP API Console";

    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    private static readonly Lazy<string> GuideHtml = new(() => MarkdownLite.ToHtml(ReadResource("guide/how-to-call-the-sterp-apis.md")));

    /// <summary>GET / - the catalogue of the thirteen documents (FR-002) and the two web services.</summary>
    public static string Home(DocumentCatalog catalog, HostsStatus status) =>
        Page(catalog, status, "overview", ConsoleTitle, html => Overview(html, catalog, status));

    /// <summary>GET /guide - contracts sections 4 and 5 (FR-STERP-PORTAL-013).</summary>
    public static string Guide(DocumentCatalog catalog, HostsStatus status) =>
        Page(catalog, status, "guide", "How to call the STERP APIs - " + ConsoleTitle,
            html => html.Append("<section id=\"view-guide\" class=\"doc\">\n").Append(GuideHtml.Value).Append("</section>\n"));

    private static string Page(DocumentCatalog catalog, HostsStatus status, string view, string title, Action<StringBuilder> main)
    {
        var html = new StringBuilder(200_000);
        html.Append("<!DOCTYPE html>\n<html lang=\"en-GB\">\n<head>\n<meta charset=\"utf-8\" />\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n")
            .Append("<title>").Append(E(title)).Append("</title>\n")
            .Append("<script src=\"/console/activity.js\"></script>\n")
            .Append("<link rel=\"stylesheet\" href=\"/console/console.css\" />\n")
            .Append("<link rel=\"icon\" type=\"image/svg+xml\" href=\"/console/favicon.svg\" />\n")
            .Append("</head>\n<body>\n<div class=\"shell\">\n")
            .Append("<div class=\"brand\"><span>API Console</span><small>STERP Supply Chain Management</small></div>\n");
        Topbar(html, catalog, status);
        Sidebar(html, catalog, status, view);
        html.Append("<main class=\"main\" id=\"main\" tabindex=\"-1\">\n");
        main(html);
        html.Append("<section id=\"view-app\" hidden></section>\n</main>\n</div>\n")
            .Append("<script id=\"console-config\" type=\"application/json\">")
            .Append(new JsonObject
            {
                ["view"] = view,
                ["apiBaseUrl"] = catalog.ApiBaseUrl,
                ["jobsBaseUrl"] = catalog.JobsBaseUrl,
                ["documents"] = new JsonArray(catalog.Documents.Select(d => (JsonNode)new JsonObject
                {
                    ["name"] = d.Name,
                    ["title"] = d.Title,
                    ["wave"] = d.Wave,
                    ["webService"] = d.WebService,
                    ["operations"] = d.OperationCount,
                }).ToArray()),
                ["status"] = status.ToJson(catalog),
            }.ToJsonString())
            .Append("</script>\n<script src=\"/console/console.js\"></script>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void Topbar(StringBuilder html, DocumentCatalog catalog, HostsStatus status)
    {
        html.Append("<div class=\"topbar\">\n");
        foreach (var h in status.Hosts)
        {
            html.Append("<span class=\"metric\" data-host=\"").Append(h.Id).Append("\" title=\"").Append(E(HostTitle(h))).Append("\">")
                .Append("<span class=\"dot-live ").Append(h.Live ? "up" : "down").Append("\"></span>")
                .Append(E(h.Name)).Append(" <strong>").Append(h.Port.ToString(CI)).Append("</strong></span>\n");
        }
        var delivered = catalog.Documents.Count(d => status.StateOf(d.Name) == "delivered");
        html.Append("<span class=\"metric\"><strong id=\"delivered-count\">").Append(delivered.ToString(CI)).Append("</strong> of ")
            .Append(catalog.Documents.Count.ToString(CI)).Append(" documents delivered</span>\n")
            .Append("<span class=\"spacer\"></span>\n")
            .Append("<a class=\"metric context\" id=\"context-metric\" href=\"/#/settings\" ")
            .Append("title=\"Try it out settings: the context headers prefilled on every operation\">Context <strong id=\"context-value\">...</strong></a>\n")
            .Append("<span class=\"spinner\" id=\"busy\" aria-hidden=\"true\" hidden></span>\n")
            .Append("<button class=\"btn sm\" type=\"button\" id=\"refresh\">Refresh</button>\n</div>\n");
    }

    private static void Sidebar(StringBuilder html, DocumentCatalog catalog, HostsStatus status, string view)
    {
        html.Append("<div class=\"sidebar\">\n<div class=\"side-search\">\n")
            .Append("<input type=\"text\" id=\"filter\" placeholder=\"Filter by path, operation, summary or FR id\" ")
            .Append("aria-label=\"Filter operations\" autocomplete=\"off\" spellcheck=\"false\" />\n")
            .Append("<select id=\"definition\" aria-label=\"Select a definition\">\n<option value=\"\">All definitions</option>\n");
        foreach (var d in catalog.Documents)
        {
            html.Append("<option value=\"").Append(E(d.Name)).Append("\">").Append(E(d.Name)).Append(" - ").Append(E(d.ShortTitle)).Append("</option>\n");
        }
        html.Append("</select>\n</div>\n<nav class=\"side-nav\" aria-label=\"Console\">\n");
        Nav(html, "/#/", "overview", view, "Overview", catalog.OperationCount.ToString(CI));
        Nav(html, "/#/drift", "drift", view, "Contract drift", null);
        Nav(html, "/#/settings", "settings", view, "Try it out settings", null);
        Nav(html, "/guide", "guide", view, "How to call the STERP APIs", null);
        html.Append("</nav>\n<nav class=\"side-tree\" aria-label=\"Operations\">\n");
        foreach (var d in catalog.Documents)
        {
            html.Append("<section class=\"side-doc").Append(status.StateOf(d.Name) == "planned" ? " is-planned" : "")
                .Append("\" data-doc=\"").Append(E(d.Name)).Append("\">\n")
                .Append("<a class=\"side-service\" href=\"/#/").Append(U(d.Name)).Append("\" title=\"").Append(E(d.Title)).Append("\">")
                .Append(E(d.Name)).Append("<span class=\"port\">").Append(E(d.Wave)).Append("</span></a>\n");
            foreach (var tag in d.Tags)
            {
                var operations = d.Operations.Where(o => o.Tags.Contains(tag.Name)).ToList();
                if (operations.Count == 0) continue;
                html.Append("<div class=\"side-tag\">\n<a class=\"side-group\" href=\"/#/").Append(U(d.Name)).Append("/tags/").Append(U(tag.Name))
                    .Append("\">").Append(E(tag.Name)).Append("</a>\n");
                foreach (var op in operations)
                {
                    html.Append("<a class=\"side-item\" href=\"/#/").Append(U(d.Name)).Append('/').Append(U(op.OperationId))
                        .Append("\" data-op=\"").Append(E(op.OperationId)).Append("\" data-search=\"").Append(E(Search(op)))
                        .Append("\" title=\"").Append(E($"{op.OperationId} - {op.Summary}")).Append("\">")
                        .Append("<span class=\"verb sm ").Append(op.Method.ToLowerInvariant()).Append("\">").Append(op.Method).Append("</span>")
                        .Append("<span class=\"path\">").Append(BreakablePath(op.Path)).Append("</span>")
                        .Append("<span class=\"dot\" title=\"Planned: answers 501 API0501 until the slice is delivered\"></span></a>\n");
                }
                html.Append("</div>\n");
            }
            html.Append("</section>\n");
        }
        html.Append("<div class=\"side-group side-empty\" id=\"side-empty\" hidden></div>\n</nav>\n</div>\n");
    }

    private static void Nav(StringBuilder html, string href, string id, string view, string text, string? count)
    {
        html.Append("<a href=\"").Append(href).Append("\" data-nav=\"").Append(id).Append('"');
        if (id == view) html.Append(" class=\"is-active\" aria-current=\"page\"");
        html.Append('>').Append(E(text));
        if (count is not null) html.Append(" <span class=\"count\">").Append(count).Append("</span>");
        html.Append("</a>\n");
    }

    private static void Overview(StringBuilder html, DocumentCatalog catalog, HostsStatus status)
    {
        var states = catalog.Documents.ToDictionary(d => d.Name, d => status.StateOf(d.Name));
        var total = catalog.OperationCount;
        var deliveredOps = catalog.Documents.Where(d => states[d.Name] == "delivered").Sum(d => d.OperationCount);
        var plannedOps = catalog.Documents.Where(d => states[d.Name] == "planned").Sum(d => d.OperationCount);

        html.Append("<section id=\"view-overview\">\n<h1>STERP APIs</h1>\n<p class=\"lede\">")
            .Append("Thirteen OpenAPI documents describe the modernized STERP: one for each of the eleven business slices and one for each ")
            .Append("web service. Pick an operation on the left to read its contract and legacy traceability, and call it under your own ")
            .Append("Windows identity.</p>\n<div class=\"stat-row\">\n")
            .Append(Stat(total, "operations", null, ""))
            .Append(Stat(states.Values.Count(s => s == "delivered"), "documents delivered", "delivered", ""))
            .Append(Stat(states.Values.Count(s => s == "planned"), "documents planned", "planned", " warn"))
            .Append(Stat(status.Hosts.Count, "web services", null, ""))
            .Append("</div>\n<div class=\"bar\" id=\"delivery-bar\" role=\"img\" aria-label=\"").Append(deliveredOps.ToString(CI)).Append(" of ")
            .Append(total.ToString(CI)).Append(" operations delivered\"><span class=\"done\" style=\"width:").Append(Pct(deliveredOps, total))
            .Append("%\"></span><span class=\"todo\" style=\"width:").Append(Pct(plannedOps, total)).Append("%\"></span></div>\n");

        html.Append("<h2>Web services</h2>\n");
        foreach (var h in status.Hosts) HostPanel(html, h);

        html.Append("<h2>OpenAPI documents</h2>\n<div class=\"panel\">\n<div class=\"body tight\">\n<table class=\"grid\" id=\"documents\">\n<thead><tr>")
            .Append("<th>Document</th><th>Title</th><th>Web service</th><th>Wave</th><th class=\"num\">Operations</th><th>State</th><th>Download</th>")
            .Append("</tr></thead>\n<tbody>\n");
        foreach (var d in catalog.Documents)
        {
            var name = E(d.Name);
            html.Append("<tr><td class=\"nowrap\"><a class=\"mono\" href=\"/#/").Append(U(d.Name)).Append("\">").Append(name).Append("</a></td>")
                .Append("<td>").Append(E(d.Title)).Append("</td>")
                .Append("<td class=\"nowrap\">").Append(E(d.WebService)).Append("</td>")
                .Append("<td class=\"nowrap\">").Append(E(d.Wave)).Append("</td>")
                .Append("<td class=\"num\">").Append(d.OperationCount.ToString(CI)).Append("</td>")
                .Append("<td class=\"nowrap\">").Append(StateBadge(d.Name, states[d.Name])).Append("</td>")
                .Append("<td class=\"nowrap\"><a href=\"/docs/").Append(name).Append(".yaml\">YAML</a> &middot; ")
                .Append("<a href=\"/docs/").Append(name).Append(".json\">JSON</a></td></tr>\n");
        }
        html.Append("</tbody>\n</table>\n</div>\n</div>\n")
            .Append("<div class=\"note\"><span class=\"icon\">&#8505;</span><span>delivered = served by the running STERP.Api or STERP.Jobs ")
            .Append("host; planned = binding document only - its operations answer 501 with reason code API0501 until the slice is delivered.")
            .Append("</span></div>\n<div id=\"problems\">");
        foreach (var p in status.Problems)
        {
            html.Append("<div class=\"note err\"><span class=\"icon\">!</span><span>").Append(E(p)).Append("</span></div>");
        }
        html.Append("</div>\n<h2>Calling the APIs</h2>\n<div class=\"note evidence\"><span class=\"icon\">&#10003;</span><span>")
            .Append("<strong>Calls run as you</strong>Every operation page sends its request from this browser, with your Windows credentials, ")
            .Append("to the server the operation declares, so STERP.Api and STERP.Jobs see your own identity. The context headers are prefilled ")
            .Append("from <a href=\"/#/settings\">Try it out settings</a>, and every request is also shown as bash, Command Prompt and PowerShell ")
            .Append("command lines. Return codes and headers are explained in <a href=\"/guide\">How to call the STERP APIs</a>.</span></div>\n")
            .Append("</section>\n");
    }

    private static void HostPanel(StringBuilder html, HostState h)
    {
        var role = h.Id == "api"
            ? "Online operations of the eleven business slices and the platform services."
            : "Batch operations: job submission, monitoring, restart, SYSOUT and spool of the batch programs and job streams.";
        html.Append("<div class=\"panel\" data-host-panel=\"").Append(h.Id).Append("\">\n<header><span class=\"dot-live ")
            .Append(h.Live ? "up" : "down").Append("\"></span>").Append(E(h.Name))
            .Append("<span class=\"spacer\"></span><span class=\"badge op\">port ").Append(h.Port.ToString(CI)).Append("</span></header>\n")
            .Append("<div class=\"body\">\n<p class=\"panel-text\">").Append(E(role)).Append("</p>\n")
            .Append("<div class=\"note err\" data-host-down").Append(h.Live ? " hidden" : "").Append("><span class=\"icon\">!</span><span>")
            .Append("<strong>Not running</strong><span data-host-detail>").Append(E(h.Live ? "" : h.Detail)).Append("</span> Start it with ")
            .Append("<code>scripts/run-local.ps1</code> or <code>scripts/run-host.ps1 -Name ").Append(h.Id == "api" ? "Api" : "Jobs")
            .Append("</code> before using Try it out.</span></div>\n<dl class=\"kv\">\n")
            .Append("<dt>base address</dt><dd>").Append(E(h.BaseUrl)).Append("</dd>\n")
            .Append("<dt>readiness</dt><dd data-host-ready>").Append(E(Readiness(h))).Append("</dd>\n")
            .Append("<dt>documents served</dt><dd data-host-docs>").Append(E(Served(h))).Append("</dd>\n")
            .Append("<dt>openapi index</dt><dd>").Append(E(h.BaseUrl + "/openapi/index.json")).Append("</dd>\n</dl>\n</div>\n</div>\n");
    }

    /// <summary>Same wording as console.js applyStatus, which rewrites these texts on Refresh.</summary>
    private static string Readiness(HostState h) =>
        !h.Live ? "not answering" : h.Detail.Length > 0 ? $"{h.Readiness} - {h.Detail}" : h.Readiness;

    private static string Served(HostState h) =>
        !h.IndexRead ? "unknown" : h.Documents.Count == 0 ? "none" : $"{h.Documents.Count}: {string.Join(", ", h.Documents)}";

    private static string HostTitle(HostState h) =>
        h.Live ? $"{h.Name} at {h.BaseUrl}: {h.Readiness}" : $"{h.Name} at {h.BaseUrl} is not answering. {h.Detail}";

    private static string StateBadge(string name, string state)
    {
        var cls = state switch { "delivered" => "ok", "planned" => "deferred", _ => "op" };
        return $"<span class=\"badge {cls}\" data-state-for=\"{E(name)}\">{state}</span>";
    }

    private static string Stat(int n, string label, string? key, string cls) =>
        $"<div class=\"stat{cls}\"><div class=\"n\"{(key is null ? "" : $" data-stat=\"{key}\"")}>{n.ToString(CI)}</div><div class=\"l\">{label}</div></div>\n";

    private static string Pct(int part, int total) =>
        total == 0 ? "0" : (part * 100.0 / total).ToString("0.##", CI);

    private static string Search(PortalOperation op) =>
        $"{op.Method} {op.Path} {op.OperationId} {op.Summary} {string.Join(' ', op.Tags)} {string.Join(' ', op.Requirements)} {op.Document}"
            .ToLowerInvariant();

    /// <summary>Long paths wrap only between their segments.</summary>
    private static string BreakablePath(string path)
    {
        var segments = path.Split('/').Skip(1).Select(s => "<span class=\"seg\">/" + E(s) + "</span>");
        return string.Join("<wbr>", segments);
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static string U(string text) => Uri.EscapeDataString(text);

    private static string ReadResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
