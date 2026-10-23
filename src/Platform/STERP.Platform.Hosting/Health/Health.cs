using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Hosting.Health;

/// <summary>An extra readiness check contributed by a host (output folders, job queue).</summary>
public interface IReadinessCheck
{
    string Name { get; }

    Task<(string Status, string? Description, JsonObject? Data)> CheckAsync(CancellationToken ct);
}

/// <summary>
/// Schema drift check (contracts 6.11, FR-STERP-API-001): the embedded frozen contract snapshot (objects, columns, types,
/// nullability, procedure parameters, indexes) compared with sys.objects/sys.columns/sys.parameters for every object owned by a
/// delivered slice. Missing or changed objects fail readiness; extra columns/parameters are reported, not failed.
/// </summary>
public sealed class SchemaDriftCheck(ISqlConnectionFactory factory)
{
    private static readonly Lazy<JsonObject?> Snapshot = new(() =>
    {
        using var s = typeof(SchemaDriftCheck).Assembly.GetManifestResourceStream("contracts/frozen-schema.json");
        return s is null ? null : JsonNode.Parse(s) as JsonObject;
    });

    public static bool HasSnapshot => Snapshot.Value is not null;

    private const string CatalogQuery = """
        SET NOCOUNT ON;
        SELECT (
         SELECT s.name AS [schema], o.name AS [name], o.type AS [type],
           (SELECT c.name, TYPE_NAME(c.user_type_id) AS type, c.max_length AS maxLength, c.precision, c.scale, c.is_nullable AS nullable
              FROM sys.columns c WHERE c.object_id = o.object_id FOR JSON PATH) AS columns,
           (SELECT p.name, TYPE_NAME(p.user_type_id) AS type, p.max_length AS maxLength, p.precision, p.scale, p.is_output AS [output]
              FROM sys.parameters p WHERE p.object_id = o.object_id FOR JSON PATH) AS parameters,
           (SELECT i.name FROM sys.indexes i WHERE i.object_id = o.object_id AND i.name IS NOT NULL FOR JSON PATH) AS indexes
         FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
         WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V','P','FN','IF','TF','TR')
         FOR JSON PATH
        ) AS j, DB_NAME() AS db;
        """;

    public async Task<(List<string> Differences, List<string> Reported, int Checked)> CompareAsync(IReadOnlyCollection<string> deliveredSlices, CancellationToken ct)
    {
        var differences = new List<string>();
        var reported = new List<string>();
        var snapshot = Snapshot.Value;
        if (snapshot is null)
        {
            differences.Add("frozen contract snapshot is not embedded in this build");
            return (differences, reported, 0);
        }
        string json;
        string db;
        await using (var cn = factory.Create())
        {
            await cn.OpenAsync(ct);
            await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(CatalogQuery, cn) { CommandTimeout = 60 };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var sb = new System.Text.StringBuilder();
            db = "";
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0)) sb.Append(reader.GetString(0));
                db = reader.GetString(1);
            }
            json = sb.ToString();
        }
        var live = (JsonNode.Parse(json.Length == 0 ? "[]" : json) as JsonArray ?? [])
            .OfType<JsonObject>()
            .ToDictionary(o => $"{o["schema"]}.{o["name"]}", o => o, StringComparer.OrdinalIgnoreCase);

        var checkedCount = 0;
        foreach (var node in snapshot["objects"]!.AsArray().OfType<JsonObject>())
        {
            var slice = node["slice"]!.GetValue<string>();
            if (!deliveredSlices.Contains(slice)) continue;
            checkedCount++;
            var name = node["object"]!.GetValue<string>();
            var kind = node["kind"]!.GetValue<string>();
            switch (kind)
            {
                case "DATABASE":
                    if (!string.Equals(db, name, StringComparison.OrdinalIgnoreCase)) differences.Add($"{name}: connected database is {db}");
                    break;
                case "INDEX":
                {
                    var table = node["table"]!.GetValue<string>();
                    var index = node["index"]!.GetValue<string>();
                    if (!live.TryGetValue(table, out var t) || !(t["indexes"] as JsonArray ?? []).Any(i => string.Equals(i?["name"]?.GetValue<string>(), index, StringComparison.OrdinalIgnoreCase)))
                    {
                        differences.Add($"{name}: index {index} on {table} is missing");
                    }
                    break;
                }
                default:
                    if (!live.TryGetValue(name, out var obj))
                    {
                        differences.Add($"{name}: {kind.ToLowerInvariant()} is missing");
                        break;
                    }
                    Compare(name, "column", node["columns"] as JsonArray, obj["columns"] as JsonArray, ["type", "maxLength", "precision", "scale", "nullable"], differences, reported);
                    Compare(name, "parameter", node["parameters"] as JsonArray, obj["parameters"] as JsonArray, ["type", "maxLength", "precision", "scale", "output"], differences, reported);
                    break;
            }
        }
        return (differences, reported, checkedCount);
    }

    private static void Compare(string obj, string what, JsonArray? expected, JsonArray? actual, string[] fields, List<string> differences, List<string> reported)
    {
        var have = (actual ?? []).OfType<JsonObject>().ToDictionary(c => c["name"]!.GetValue<string>(), c => c, StringComparer.OrdinalIgnoreCase);
        var want = (expected ?? []).OfType<JsonObject>().ToList();
        foreach (var e in want)
        {
            var n = e["name"]!.GetValue<string>();
            if (!have.TryGetValue(n, out var a))
            {
                differences.Add($"{obj}: {what} {n} is missing");
                continue;
            }
            foreach (var f in fields)
            {
                var ev = e[f]?.ToJsonString() ?? "null";
                var av = a[f]?.ToJsonString() ?? "null";
                if (!string.Equals(ev, av, StringComparison.OrdinalIgnoreCase))
                {
                    differences.Add($"{obj}: {what} {n} {f} is {av}, frozen contract {ev}");
                }
            }
        }
        foreach (var extra in have.Keys.Except(want.Select(w => w["name"]!.GetValue<string>()), StringComparer.OrdinalIgnoreCase))
        {
            reported.Add($"{obj}: {what} {extra} is not in the frozen contract");
        }
    }
}

public static class HealthEndpoints
{
    public static void MapSterpHealth(this IEndpointRouteBuilder app, string engine)
    {
        app.MapGet("/health/live", () => Results.Json(new JsonObject { ["status"] = "Healthy", ["checks"] = new JsonArray() }, SterpJson.Options))
           .AllowAnonymous().WithName(engine == "batch" ? "jobs_getLiveness" : "api_getLiveness");

        app.MapGet("/health/ready", async (HttpContext http) =>
        {
            var report = await ReadinessAsync(http.RequestServices, http.RequestAborted);
            return Results.Json(report, SterpJson.Options, "application/json", report["status"]!.GetValue<string>() == "Healthy" ? 200 : 503);
        }).AllowAnonymous().WithName(engine == "batch" ? "jobs_getReadiness" : "api_getReadiness");
    }

    /// <summary>Readiness report: database probe (legacy PING), schema drift, host checks, delivered slices.</summary>
    public static async Task<JsonObject> ReadinessAsync(IServiceProvider services, CancellationToken ct)
    {
        var checks = new JsonArray();
        var healthy = true;
        var delivered = services.GetRequiredService<OperationRegistry>().DeliveredSlices
            .Select(s => s.StartsWith("STERP-", StringComparison.Ordinal) ? s[6..] : s).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var differences = new JsonArray();
        var reportedNode = new JsonArray();
        try
        {
            // database probe = legacy PING HEALTH (FR-STERP-PLATFORM-030)
            var probe = await services.GetRequiredService<Kernel.Services.IDatabaseProbe>().ProbeAsync(ct);
            checks.Add(Check("database", probe.Available ? "Healthy" : "Unhealthy",
                probe.Available ? $"{probe.Result.Message}: {probe.DatabaseName} at {probe.ServerTime}" : probe.Result.Message,
                new JsonObject { ["returnCode"] = probe.Result.ReturnCode, ["reasonCode"] = probe.Result.ReasonCode, ["version"] = probe.Version }));
            if (!probe.Available)
            {
                healthy = false;
                checks.Add(Check("schema-drift", "Unhealthy", "not checked: the database probe failed", null));
            }
            else
            {
                var drift = services.GetRequiredService<SchemaDriftCheck>();
                var (diffs, reported, count) = await drift.CompareAsync(delivered, ct);
                foreach (var d in diffs) differences.Add(d);
                foreach (var d in reported) reportedNode.Add(d);
                if (diffs.Count > 0) healthy = false;
                checks.Add(Check("schema-drift", diffs.Count == 0 ? "Healthy" : "Unhealthy",
                    diffs.Count == 0 ? $"{count} frozen-contract objects of the delivered slices match" : string.Join("; ", diffs),
                    new JsonObject { ["objectsChecked"] = count, ["reported"] = reportedNode }));
            }
        }
        catch (Exception ex)
        {
            healthy = false;
            checks.Add(Check("database", "Unhealthy", ex.Message, null));
        }
        foreach (var extra in services.GetServices<IReadinessCheck>())
        {
            try
            {
                var (status, description, data) = await extra.CheckAsync(ct);
                if (status != "Healthy") healthy = false;
                checks.Add(Check(extra.Name, status, description, data));
            }
            catch (Exception ex)
            {
                healthy = false;
                checks.Add(Check(extra.Name, "Unhealthy", ex.Message, null));
            }
        }
        return new JsonObject
        {
            ["status"] = healthy ? "Healthy" : "Unhealthy",
            ["checks"] = checks,
            ["deliveredSlices"] = new JsonArray(delivered.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
            ["driftDifferences"] = differences,
        };
    }

    private static JsonObject Check(string name, string status, string? description, JsonObject? data) => new()
    {
        ["name"] = name,
        ["status"] = status,
        ["description"] = description,
        ["data"] = data ?? new JsonObject(),
    };

    public static string Serialize(JsonObject o) => o.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
