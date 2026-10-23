using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Framework.Format;

/// <summary>
/// UK presentation rules of the legacy client (GridHelper.vb:89-150, Fmt): column captions humanised from the legacy column
/// names and cell formats by data type. Numbers use the UK group/decimal separators (identical to the invariant culture),
/// dates dd/MM/yyyy (FR-STERP-WEB-017).
/// </summary>
public static partial class UkFormat
{
    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>GridHelper.Humanise: CustomerCode -> "Customer Code", AmountGBP -> "Amount GBP".</summary>
    public static string Humanise(string columnName)
    {
        if (string.IsNullOrEmpty(columnName)) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < columnName.Length; i++)
        {
            var c = columnName[i];
            if (i > 0 && char.IsUpper(c) &&
                (!char.IsUpper(columnName[i - 1]) || (i + 1 < columnName.Length && char.IsLower(columnName[i + 1]))))
            {
                sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The legacy result-set column name of a camelCase JSON property (contracts 4.2): first letter upper case.</summary>
    public static string LegacyName(string jsonName) =>
        string.IsNullOrEmpty(jsonName) ? jsonName : char.ToUpperInvariant(jsonName[0]) + jsonName[1..];

    /// <summary>GridHelper.GuessType for a SQL type name of a dynamic result set (PlatformResultColumn.sqlType).</summary>
    public static string KindFromSqlType(string columnName, string sqlType)
    {
        var t = sqlType.ToLowerInvariant();
        var paren = t.IndexOf('(');
        if (paren > 0) t = t[..paren];
        return t switch
        {
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" =>
                columnName.EndsWith("At", StringComparison.Ordinal) || columnName.EndsWith("Time", StringComparison.Ordinal) ? "STAMP" : "DATE",
            "decimal" or "numeric" or "money" or "smallmoney" =>
                columnName.EndsWith("Qty", StringComparison.Ordinal) || columnName.StartsWith("Qty", StringComparison.Ordinal) ? "QTY" : "MONEY",
            "int" or "bigint" => "INT",
            "bit" => "BOOL",
            _ => "TEXT",
        };
    }

    /// <summary>GridHelper.GuessType for a JSON value (legacy column name decides QTY/MONEY and DATE/STAMP).</summary>
    public static string KindFromValue(string legacyName, JsonNode? value)
    {
        if (value is not JsonValue v) return "TEXT";
        if (v.TryGetValue<bool>(out _)) return "BOOL";
        if (v.TryGetValue<string>(out var s))
        {
            if (IsoDateTime().IsMatch(s) || IsoDate().IsMatch(s))
            {
                return legacyName.EndsWith("At", StringComparison.Ordinal) || legacyName.EndsWith("Time", StringComparison.Ordinal) ? "STAMP" : "DATE";
            }
            return "TEXT";
        }
        var raw = v.ToJsonString();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
        {
            return legacyName.EndsWith("Qty", StringComparison.Ordinal) || legacyName.StartsWith("Qty", StringComparison.Ordinal) ? "QTY" : "MONEY";
        }
        return "INT";
    }

    /// <summary>GridHelper.FormatColumn: the text a legacy grid cell shows for a value of the given column kind.</summary>
    public static string Cell(JsonNode? value, string kind)
    {
        if (value is null) return "";
        var raw = Json.Raw(value);
        switch (kind.ToUpperInvariant())
        {
            case "MONEY": return Number(raw, "#,##0.00");
            case "QTY": return Number(raw, "#,##0.000000");
            case "RATE": return Number(raw, "#,##0.00000000");
            case "PCT": return Number(raw, "#,##0.00##");
            case "INT": return Number(raw, "#,##0");
            case "DATE": return DateText(raw, "dd/MM/yyyy");
            case "STAMP": return DateText(raw, "dd/MM/yyyy HH:mm:ss");
            case "BOOL": return value is JsonValue b && b.TryGetValue<bool>(out var flag) ? (flag ? "True" : "False") : raw;
            default: return raw;
        }
    }

    /// <summary>Right-aligned numeric kinds, centred dates and tick boxes (GridHelper.FormatColumn).</summary>
    public static string Alignment(string kind) => kind.ToUpperInvariant() switch
    {
        "MONEY" or "QTY" or "RATE" or "PCT" or "INT" => "st-r",
        "DATE" or "BOOL" => "st-c",
        _ => "",
    };

    public static string Number(string raw, string format) =>
        decimal.TryParse(raw, NumberStyles.Float, Culture, out var d) ? d.ToString(format, Culture) : raw;

    public static string DateText(string raw, string format)
    {
        if (DateTime.TryParse(raw, Culture, DateTimeStyles.None, out var dt)) return dt.ToString(format, Culture);
        return raw;
    }

    /// <summary>Fmt.Date: dd/MM/yyyy.</summary>
    public static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", Culture);

    /// <summary>Fmt.Money: £#,##0.00 (a negative amount -£#,##0.00).</summary>
    public static string Money(decimal v) => v.ToString("£#,##0.00;-£#,##0.00;£0.00", Culture);

    /// <summary>Fmt.Qty: six decimals with trailing zeros trimmed, then the unit of measure when one is given.</summary>
    public static string Qty(decimal v, string? uom = null)
    {
        var s = v.ToString("#,##0.000000", Culture);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        var u = (uom ?? "").TrimEnd();
        return u.Length == 0 ? s : s + " " + u;
    }

    /// <summary>
    /// The DataTable column name CommArea.GetTable gives an OCCURS field ("tMoveId" with prefix "LS-" -> "LS-T-MOVE-ID"):
    /// the copybook field names, which GridHelper.Humanise leaves as they are.
    /// </summary>
    public static string CopybookName(string prefix, string jsonName)
    {
        var sb = new StringBuilder(prefix);
        for (var i = 0; i < jsonName.Length; i++)
        {
            var c = jsonName[i];
            if (i > 0 && char.IsUpper(c)) sb.Append('-');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    public static string Date(DateTime d) => d.ToString("dd/MM/yyyy", Culture);

    /// <summary>Fmt.ParseDate: dd/MM/yyyy, dd/MM/yy, ddMMyyyy, yyyyMMdd, dd-MM-yyyy, dd.MM.yyyy (and d/M/yyyy, yyyy-MM-dd); null when not a date.</summary>
    public static DateOnly? ParseDate(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        string[] formats = ["dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "ddMMyyyy", "yyyyMMdd", "dd-MM-yyyy", "dd.MM.yyyy", "yyyy-MM-dd"];
        return DateOnly.TryParseExact(t, formats, Culture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Fmt.Yyyymmdd.</summary>
    public static string Yyyymmdd(DateOnly d) => d.ToString("yyyyMMdd", Culture);

    /// <summary>Text with the Windows access-key marker removed: "Stoc&amp;k Control" -> "Stock Control" ("&amp;&amp;" stays one "&amp;").</summary>
    public static string StripMnemonic(string caption)
    {
        if (string.IsNullOrEmpty(caption)) return caption ?? "";
        var sb = new StringBuilder(caption.Length);
        for (var i = 0; i < caption.Length; i++)
        {
            if (caption[i] == '&')
            {
                if (i + 1 < caption.Length && caption[i + 1] == '&') { sb.Append('&'); i++; }
                continue;
            }
            sb.Append(caption[i]);
        }
        return sb.ToString();
    }

    /// <summary>The access key of a caption ("Stoc&amp;k Control" -> 'K'), or null.</summary>
    public static char? Mnemonic(string caption)
    {
        for (var i = 0; i + 1 < caption.Length; i++)
        {
            if (caption[i] != '&') continue;
            if (caption[i + 1] == '&') { i++; continue; }
            return char.ToUpperInvariant(caption[i + 1]);
        }
        return null;
    }

    /// <summary>A label caption without the access key and the trailing colon ("&amp;Movement:" -> "Movement").</summary>
    public static string LabelText(string caption) => StripMnemonic(caption).TrimEnd().TrimEnd(':').TrimEnd();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}")]
    private static partial Regex IsoDateTime();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDate();
}
