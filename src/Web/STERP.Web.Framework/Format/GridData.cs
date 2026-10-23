using System.Text.Json.Nodes;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Framework.Format;

/// <summary>One grid column: JSON property, legacy caption, format kind (GridHelper data types) and visibility.</summary>
public sealed class GridColumn
{
    public GridColumn(string name, string? caption = null, string? kind = null, string? legacyName = null)
    {
        Name = name;
        LegacyName = legacyName ?? (char.IsLower(name.FirstOrDefault()) ? UkFormat.LegacyName(name) : name);
        Caption = caption ?? UkFormat.Humanise(LegacyName);
        Kind = kind ?? "";
    }

    /// <summary>Property name in the JSON rows.</summary>
    public string Name { get; }

    /// <summary>Column name of the legacy result set (drives the caption and the QTY/MONEY, DATE/STAMP guesses).</summary>
    public string LegacyName { get; }

    public string Caption { get; set; }

    /// <summary>TEXT, CODE, MONEY, QTY, RATE, PCT, INT, DATE, STAMP, BOOL (empty = guess from the values).</summary>
    public string Kind { get; set; }

    public bool Visible { get; set; } = true;

    /// <summary>Fixed width in pixels (0 = sized by content), core.MaintColumn.Width.</summary>
    public int Width { get; set; }
}

/// <summary>
/// Rows bound to a legacy-style grid (GridHelper.Bind): columns in result-set order, captions humanised from the column
/// names, UK formats by type, the last column filling the width.
/// </summary>
public sealed class GridData
{
    public GridData(IEnumerable<GridColumn> columns, IEnumerable<JsonObject> rows)
    {
        Columns = columns.ToList();
        Rows = rows.ToList();
        foreach (var c in Columns.Where(c => c.Kind.Length == 0))
        {
            var sample = Rows.Select(r => r[c.Name]).FirstOrDefault(v => v is not null);
            c.Kind = UkFormat.KindFromValue(c.LegacyName, sample);
        }
    }

    public static GridData Empty { get; } = new([], []);

    public List<GridColumn> Columns { get; }

    public List<JsonObject> Rows { get; }

    public int Count => Rows.Count;

    /// <summary>A dynamic result set (<c>{ columns: [{name, sqlType}], items }</c>, PlatformDynamicRows): SQL types drive the formats.</summary>
    public static GridData FromResultSet(JsonNode? body)
    {
        var columns = new List<GridColumn>();
        foreach (var c in body?["columns"] as JsonArray ?? [])
        {
            var name = Json.Str(c?["name"]);
            columns.Add(new GridColumn(name, kind: UkFormat.KindFromSqlType(name, Json.Str(c?["sqlType"])), legacyName: name));
        }
        var rows = (body?["items"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (columns.Count == 0 && rows.Count > 0)
        {
            columns.AddRange(rows[0].Select(p => new GridColumn(p.Key, legacyName: p.Key)));
        }
        return new GridData(columns, rows);
    }

    /// <summary>A direct list (<c>{ items, count }</c> or an array) of camelCase rows; explicit columns win over inference.</summary>
    public static GridData FromItems(JsonNode? body, params GridColumn[] columns)
    {
        var items = body as JsonArray ?? body?["items"] as JsonArray ?? [];
        var rows = items.OfType<JsonObject>().ToList();
        var cols = columns.Length > 0 ? columns.ToList()
            : rows.Count > 0 ? rows[0].Select(p => new GridColumn(p.Key)).ToList() : [];
        return new GridData(cols, rows);
    }

    /// <summary>
    /// The rows of a COMMAREA OCCURS group (CommArea.GetTable): columns named after the copybook fields
    /// (<paramref name="prefix"/> + field), numeric fields typed decimal and therefore shown as MONEY by GridHelper.GuessType -
    /// including <c>PIC 9(8)</c> dates, which the API returns as ISO dates (or null for zero) and the legacy grid showed as the
    /// number yyyyMMdd. Text fields are trimmed text.
    /// </summary>
    public static GridData FromCommArea(JsonArray? items, string prefix, params string[] dateFields)
    {
        var source = (items ?? []).OfType<JsonObject>().ToList();
        if (source.Count == 0) return Empty;
        var names = source[0].Select(p => p.Key).ToList();
        var numeric = names.ToDictionary(n => n, n => dateFields.Contains(n, StringComparer.Ordinal) ||
            source.Any(r => r[n] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number));
        var rows = new List<JsonObject>();
        foreach (var r in source)
        {
            var row = new JsonObject();
            foreach (var n in names)
            {
                var value = r[n];
                if (dateFields.Contains(n, StringComparer.Ordinal))
                {
                    var text = Json.Str(value).Replace("-", "", StringComparison.Ordinal);
                    row[n] = JsonValue.Create(decimal.TryParse(text, System.Globalization.NumberStyles.Integer, UkFormat.Culture, out var d) ? d : 0m);
                }
                else if (numeric[n])
                {
                    row[n] = JsonValue.Create(Json.Dec(value));
                }
                else
                {
                    row[n] = JsonValue.Create(Json.Str(value).TrimEnd());
                }
            }
            rows.Add(row);
        }
        var columns = names.Select(n => new GridColumn(n, kind: numeric[n] ? "MONEY" : "TEXT", legacyName: UkFormat.CopybookName(prefix, n))).ToList();
        return new GridData(columns, rows);
    }

    /// <summary>GridHelper.Decorate: caption, data type and width registered in core.MaintColumn.</summary>
    public void Decorate(string columnName, string caption, string dataType, int width)
    {
        var c = Columns.FirstOrDefault(x => string.Equals(x.Name, columnName, StringComparison.OrdinalIgnoreCase));
        if (c is null) return;
        c.Caption = caption;
        if (width > 0) c.Width = width;
        var kind = dataType.ToUpperInvariant();
        // FormatColumn only knows these kinds; any other data type keeps the cell's own text
        c.Kind = kind is "MONEY" or "QTY" or "RATE" or "PCT" or "INT" or "DATE" or "STAMP" or "BOOL" ? kind : "TEXT";
    }

    public void Hide(string columnName)
    {
        var c = Columns.FirstOrDefault(x => string.Equals(x.Name, columnName, StringComparison.OrdinalIgnoreCase));
        if (c is not null) c.Visible = false;
    }

    public JsonNode? Value(int row, string column) =>
        row >= 0 && row < Rows.Count ? Rows[row].FirstOrDefault(p => string.Equals(p.Key, column, StringComparison.OrdinalIgnoreCase)).Value : null;

    public string Text(int row, string column) => Json.Raw(Value(row, column));

    public string Cell(JsonObject row, GridColumn column) => UkFormat.Cell(row[column.Name], column.Kind);
}
