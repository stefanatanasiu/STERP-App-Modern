using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Sterp.ApiPortal.Portal;

/// <summary>
/// Renders the small Markdown subset of the reference page (headings, paragraphs, bullet lists, tables, fenced code,
/// code spans, bold, italics, links) to HTML. Everything is HTML-encoded first; no raw HTML passes through.
/// </summary>
public static partial class MarkdownLite
{
    public static string ToHtml(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var html = new StringBuilder();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                i++;
                continue;
            }
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i++]);
                i++;
                html.Append("<pre class=\"code\"><code>").Append(WebUtility.HtmlEncode(string.Join("\n", code))).Append("</code></pre>\n");
                continue;
            }
            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var text = heading.Groups[2].Value.Trim();
                html.Append($"<h{level} id=\"{Slug(text)}\">").Append(Inline(text)).Append($"</h{level}>\n");
                i++;
                continue;
            }
            if (line.TrimStart().StartsWith('|'))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|')) rows.Add(lines[i++].Trim());
                AppendTable(html, rows);
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                html.Append("<ul>\n");
                while (i < lines.Length && lines[i].StartsWith("- ", StringComparison.Ordinal))
                {
                    var item = new StringBuilder(lines[i][2..].Trim());
                    i++;
                    while (i < lines.Length && lines[i].StartsWith("  ", StringComparison.Ordinal) && lines[i].Trim().Length > 0)
                    {
                        item.Append(' ').Append(lines[i].Trim());
                        i++;
                    }
                    html.Append("<li>").Append(Inline(item.ToString())).Append("</li>\n");
                }
                html.Append("</ul>\n");
                continue;
            }
            var paragraph = new StringBuilder(line.Trim());
            i++;
            while (i < lines.Length && lines[i].Trim().Length > 0 && !lines[i].StartsWith("```", StringComparison.Ordinal) &&
                   !HeadingRegex().IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith('|') && !lines[i].StartsWith("- ", StringComparison.Ordinal))
            {
                paragraph.Append(' ').Append(lines[i].Trim());
                i++;
            }
            html.Append("<p>").Append(Inline(paragraph.ToString())).Append("</p>\n");
        }
        return html.ToString();
    }

    private static void AppendTable(StringBuilder html, List<string> rows)
    {
        var body = rows.Where(r => !SeparatorRegex().IsMatch(r)).ToList();
        if (body.Count == 0) return;
        html.Append("<table class=\"grid\">\n<thead><tr>");
        foreach (var cell in Cells(body[0])) html.Append("<th>").Append(Inline(cell)).Append("</th>");
        html.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in body.Skip(1))
        {
            html.Append("<tr>");
            foreach (var cell in Cells(row)) html.Append("<td>").Append(Inline(cell)).Append("</td>");
            html.Append("</tr>\n");
        }
        html.Append("</tbody></table>\n");
    }

    /// <summary>Splits a table row on '|' outside code spans.</summary>
    private static List<string> Cells(string row)
    {
        var text = row.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|')) text = text[..^1];
        var cells = new List<string>();
        var current = new StringBuilder();
        var inCode = false;
        foreach (var c in text)
        {
            if (c == '`') inCode = !inCode;
            if (c == '|' && !inCode)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static string Inline(string text)
    {
        var codes = new List<string>();
        var encoded = CodeRegex().Replace(text, m =>
        {
            codes.Add("<code>" + WebUtility.HtmlEncode(m.Groups[1].Value) + "</code>");
            return "@@c" + (codes.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "@@";
        });
        encoded = WebUtility.HtmlEncode(encoded);
        encoded = LinkRegex().Replace(encoded, m => $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
        encoded = BoldRegex().Replace(encoded, "<strong>$1</strong>");
        encoded = ItalicRegex().Replace(encoded, "<em>$1</em>");
        return PlaceholderRegex().Replace(encoded, m => codes[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private static string Slug(string text) =>
        SlugRegex().Replace(text.ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex(@"^(#{1,4})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\|?\s*:?-{3,}")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+|/[^)\s]*|#[^)\s]*)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\*\*(\S(?:.*?\S)?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<![\w*])\*(\S(?:[^*]*?\S)?)\*(?![\w*])")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"@@c(\d+)@@")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
