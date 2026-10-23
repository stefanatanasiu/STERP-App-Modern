using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Hosting.PlatformContracts;

/// <summary>One column of a metadata-driven result set (PlatformResultColumn).</summary>
public sealed record ResultColumn(string Name, string SqlType);

/// <summary>
/// Metadata-driven result set (PlatformDynamicRows; decision D-PLAT-03): rows keyed by the exact legacy column name or query
/// alias, values typed by contracts 4.3, columns in result-set order. Used by generic maintenance and by every lookup.
/// </summary>
public sealed class DynamicRows
{
    public DynamicRows(IReadOnlyList<ResultColumn> columns, JsonArray items)
    {
        Columns = columns;
        Items = items;
    }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public JsonArray Items { get; }

    public static DynamicRows Empty => new([], []);

    /// <summary>The binding JSON shape { columns, items, count }.</summary>
    public JsonObject ToJson() => new()
    {
        ["columns"] = new JsonArray(Columns.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["sqlType"] = c.SqlType }).ToArray()),
        ["items"] = Items.DeepClone(),
        ["count"] = Items.Count,
    };

    /// <summary>Reads the current result set of <paramref name="reader"/> (at most <paramref name="maxRows"/> rows when given).</summary>
    public static async Task<DynamicRows> ReadAsync(DbDataReader reader, int? maxRows = null, CancellationToken ct = default)
    {
        var schema = reader.CanGetColumnSchema() ? reader.GetColumnSchema() : null;
        var columns = new List<ResultColumn>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(new ResultColumn(reader.GetName(i), SqlTypeOf(schema?[i], reader.GetDataTypeName(i))));
        }
        var items = new JsonArray();
        while ((maxRows is null || items.Count < maxRows) && await reader.ReadAsync(ct))
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var key = columns[i].Name.Length == 0 ? $"column{i + 1}" : columns[i].Name;
                if (row.ContainsKey(key)) continue;
                row[key] = reader.IsDBNull(i) ? null : SqlJson.ToNode(reader.GetDataTypeName(i), reader.GetValue(i));
            }
            items.Add(row);
        }
        return new DynamicRows(columns, items);
    }

    /// <summary>SQL Server type name with its length, precision/scale or fractional-second scale, e.g. varchar(30), decimal(18,2).</summary>
    public static string SqlTypeOf(DbColumn? column, string dataTypeName)
    {
        var name = dataTypeName.ToLowerInvariant();
        if (column is null) return name;
        switch (name)
        {
            case "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary":
                var size = column.ColumnSize ?? 0;
                return size is <= 0 or >= 1_073_741_823 ? $"{name}(max)" : $"{name}({size.ToString(CultureInfo.InvariantCulture)})";
            case "decimal" or "numeric":
                return $"{name}({(column.NumericPrecision ?? 18).ToString(CultureInfo.InvariantCulture)},{(column.NumericScale ?? 0).ToString(CultureInfo.InvariantCulture)})";
            case "datetime2" or "time" or "datetimeoffset":
                return column.NumericScale is { } s and < 8 ? $"{name}({s.ToString(CultureInfo.InvariantCulture)})" : name;
            default:
                return name;
        }
    }
}
