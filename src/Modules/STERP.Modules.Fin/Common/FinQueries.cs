using System.Text.Json.Nodes;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Common;

/// <summary>SqlHelper-family reads (IProcedureRunner) returning rows named per the FIN binding document.</summary>
public static class FinQueries
{
    /// <summary>SqlHelper.ExecuteDataTable: the first result set as JSON rows.</summary>
    public static Task<JsonArray> TableAsync(this IProcedureRunner sql, string procedureOrSql, object? param, CancellationToken ct = default) =>
        sql.WithReaderAsync(procedureOrSql, param, r => FinJson.ReadRowsAsync(r, ct), ct);

    /// <summary>SqlHelper.ExecuteRow: the first row of the first result set, or null.</summary>
    public static Task<JsonObject?> RowAsync(this IProcedureRunner sql, string procedureOrSql, object? param, CancellationToken ct = default) =>
        sql.WithReaderAsync(procedureOrSql, param, r => FinJson.ReadFirstAsync(r, ct), ct);

    /// <summary>SqlHelper.ExecuteDataSet: every result set as JSON rows.</summary>
    public static Task<List<JsonArray>> SetsAsync(this IProcedureRunner sql, string procedureOrSql, object? param, CancellationToken ct = default) =>
        sql.WithReaderAsync(procedureOrSql, param, async r =>
        {
            var sets = new List<JsonArray>();
            do
            {
                if (r.FieldCount > 0) sets.Add(await FinJson.ReadRowsAsync(r, ct));
            } while (await r.NextResultAsync(ct));
            return sets;
        }, ct);

    /// <summary>The same read on the engine connection (SQLIO path).</summary>
    public static Task<JsonArray> TableAsync(this ISqlRunner sql, string text, object? param, CancellationToken ct = default) =>
        sql.WithReaderAsync(text, param, r => FinJson.ReadRowsAsync(r, ct), ct);

    /// <summary>VatService.CanAct / core.usp_CheckPermission: allowed only when a row returns Allowed = 1 (fail closed).</summary>
    public static async Task<bool> CanActAsync(this IProcedureRunner sql, string user, string permission, string company, string site, CancellationToken ct = default)
    {
        var row = await sql.RowAsync("core.usp_CheckPermission", new
        {
            UserCode = user,
            PermissionCode = permission,
            CompanyCode = company,
            SiteCode = site,
        }, ct);
        if (row is null) return false;
        var allowed = row.FirstOrDefault(p => string.Equals(p.Key, "allowed", StringComparison.OrdinalIgnoreCase)).Value;
        return allowed switch
        {
            null => false,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            JsonValue v when v.TryGetValue<int>(out var i) => i != 0,
            JsonValue v when v.TryGetValue<string>(out var s) => s.Trim() is "1" or "Y" or "y" || string.Equals(s.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
