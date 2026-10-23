using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Common;

/// <summary>
/// Result-set to JSON conversion with the property names of the binding FIN document (contracts 4.2-4.3): camelCase of the
/// column name with the legacy "GBP" suffix written "Gbp" (TotalDebitGBP -> totalDebitGbp) and RowVer -> rowVersion.
/// </summary>
public static class FinJson
{
    public static string Name(string column)
    {
        if (string.IsNullOrEmpty(column)) return column;
        if (string.Equals(column, "RowVer", StringComparison.Ordinal)) return "rowVersion";
        var name = JsonNamingPolicy.CamelCase.ConvertName(column);
        var i = name.IndexOf("GBP", StringComparison.Ordinal);
        while (i > 0)
        {
            name = string.Concat(name.AsSpan(0, i), "Gbp", name.AsSpan(i + 3));
            i = name.IndexOf("GBP", i + 3, StringComparison.Ordinal);
        }
        return name;
    }

    public static async Task<JsonArray> ReadRowsAsync(DbDataReader reader, CancellationToken ct = default)
    {
        var rows = new JsonArray();
        var names = Names(reader);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Row(reader, names));
        }
        return rows;
    }

    public static async Task<JsonObject?> ReadFirstAsync(DbDataReader reader, CancellationToken ct = default)
    {
        var names = Names(reader);
        return await reader.ReadAsync(ct) ? Row(reader, names) : null;
    }

    private static string[] Names(DbDataReader reader)
    {
        var names = new string[reader.FieldCount];
        for (var i = 0; i < names.Length; i++)
        {
            var n = reader.GetName(i);
            names[i] = string.IsNullOrEmpty(n) ? $"column{i + 1}" : Name(n);
        }
        return names;
    }

    private static JsonObject Row(DbDataReader reader, string[] names)
    {
        var row = new JsonObject();
        for (var i = 0; i < names.Length; i++)
        {
            row[names[i]] = reader.IsDBNull(i) ? null : SqlJson.ToNode(reader.GetDataTypeName(i), reader.GetValue(i));
        }
        return row;
    }
}
