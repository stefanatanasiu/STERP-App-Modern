using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Hosting.PlatformContracts;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Platform.Maintenance;

/// <summary>
/// Generic metadata-driven file maintenance (FR-STERP-PLATFORM-041, -042; STERP-PLATFORM 4.7): MaintService
/// (MaintService.vb:16-68) -> core.usp_UiMaintDefList, core.usp_UiMaintDef, core.usp_UiMaintList, core.usp_UiMaintSave.
/// </summary>
public static class MaintenanceEndpoints
{
    public static void Map(OperationRegistry ops)
    {
        ops.Handle("platform_listMaintenanceDefinitions", ListDefinitionsAsync);
        ops.Handle("platform_getMaintenanceDefinition", GetDefinitionAsync);
        ops.Handle("platform_listMaintenanceRows", ListRowsAsync);
        ops.Handle("platform_saveMaintenanceRow", SaveRowAsync);
    }

    /// <summary>MaintService.Definitions: core.usp_UiMaintDefList(@UserCode, @CompanyCode).</summary>
    private static async Task<IResult> ListDefinitionsAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("core.usp_UiMaintDefList", new
        {
            UserCode = ctx.Session.UserCode,
            CompanyCode = ctx.Session.CompanyCode,
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>MaintService.LoadDefinition: core.usp_UiMaintDef(@MaintCode, @UserCode, @CompanyCode), result sets 1 and 2.</summary>
    private static async Task<IResult> GetDefinitionAsync(OperationContext ctx)
    {
        var code = ctx.Route("maintCode");
        var sets = await ctx.Get<IProcedureRunner>().QueryJsonSetsAsync("core.usp_UiMaintDef", new
        {
            MaintCode = code,
            UserCode = ctx.Session.UserCode,
            CompanyCode = ctx.Session.CompanyCode,
        }, ctx.Aborted);
        if (sets.Count == 0 || sets[0].Count == 0 || sets[0][0] is not JsonObject definition)
        {
            return ctx.NotFound("E-0090", "Maintenance definition is not registered.", null);
        }
        definition["columns"] = sets.Count > 1 ? sets[1].DeepClone() : new JsonArray();
        return ctx.Direct(definition);
    }

    /// <summary>MaintService.List: core.usp_UiMaintList(@MaintCode, @UserCode, @CompanyCode, @Search) with the default @Top 500.</summary>
    private static async Task<IResult> ListRowsAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().WithReaderAsync("core.usp_UiMaintList", new
        {
            MaintCode = ctx.Route("maintCode"),
            UserCode = ctx.Session.UserCode,
            CompanyCode = ctx.Session.CompanyCode,
            Search = ctx.Query("search")?.Trim(),
        }, r => DynamicRows.ReadAsync(r, null, ctx.Aborted), ctx.Aborted);
        return ctx.Direct(rows.ToJson());
    }

    /// <summary>MaintService.Save through IGenericMaintenance: @Data built as MaintService.ToXml from the posted values in order.</summary>
    private static async Task<IResult> SaveRowAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var mode = body["mode"] is JsonValue m && m.TryGetValue<string>(out var ms) && ms.Length > 0
            ? ms
            : throw ApiProblemException.BadBody("mode is required.", new Dictionary<string, string[]> { ["mode"] = ["mode is required."] });
        if (body["values"] is not JsonObject values)
        {
            throw ApiProblemException.BadBody("values must be an object of column name -> value.", new Dictionary<string, string[]> { ["values"] = ["object required"] });
        }
        var list = new List<KeyValuePair<string, string?>>();
        foreach (var (column, node) in values)
        {
            list.Add(new(column, node switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "1" : "0",
                JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString(CultureInfo.InvariantCulture),
                _ => throw ApiProblemException.BadBody($"values.{column} must be a string or null."),
            }));
        }
        var saved = await ctx.Get<IGenericMaintenance>().SaveAsync(ctx.Route("maintCode"), mode, list, ctx.Aborted);
        return ctx.Direct(new JsonObject
        {
            ["rowsAffected"] = saved.RowsAffected,
            ["entityKey"] = saved.EntityKey,
            ["action"] = saved.Action,
        });
    }
}

/// <summary>IGenericMaintenance: MaintService.Save (MaintService.vb:31-43) on its own SqlHelper connection.</summary>
public sealed class GenericMaintenance(IProcedureRunner sql, SterpContext session) : IGenericMaintenance
{
    public async Task<MaintenanceSaveResult> SaveAsync(string maintCode, string mode, IReadOnlyList<KeyValuePair<string, string?>> values, CancellationToken ct = default)
    {
        var row = await sql.QueryFirstOrDefaultAsync<SaveRow>("core.usp_UiMaintSave", new
        {
            MaintCode = maintCode,
            UserCode = session.UserCode,
            CompanyCode = session.CompanyCode,
            Mode = mode.ToUpperInvariant(),
            Data = MaintenanceXml.ToXml(values),
        }, ct);
        return row is null
            ? new MaintenanceSaveResult(0, null, mode.ToUpperInvariant())
            : new MaintenanceSaveResult(row.RowsAffected ?? 0, row.EntityKey?.TrimEnd(' '), (row.Action ?? mode.ToUpperInvariant()).TrimEnd(' '));
    }

    private sealed class SaveRow
    {
        public int? RowsAffected { get; set; }
        public string? EntityKey { get; set; }
        public string? Action { get; set; }
    }
}
