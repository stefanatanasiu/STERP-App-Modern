using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Sterp.Platform.Hosting.Contracts;
using Sterp.Platform.Hosting.Health;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Platform.Diagnostics;

/// <summary>
/// Service diagnostics and status-bar indicator (FR-STERP-PLATFORM-056, -030, -055): EngineService.Ping/Status
/// (EngineService.vb:14-42) and frmEngineDiag, with the readiness result, drift check and delivered slices.
/// </summary>
public static class DiagnosticsEndpoints
{
    private static readonly HashSet<string> HostDocuments = ["STERP-API", "STERP-JOBS"];

    public static void Map(OperationRegistry ops)
    {
        ops.Handle("platform_getServiceDiagnostics", GetDiagnosticsAsync, answersWithoutDatabase: true);
        ops.Handle("platform_getServiceStatus", GetStatusAsync, answersWithoutDatabase: true);
    }

    private static async Task<IResult> GetDiagnosticsAsync(OperationContext ctx)
    {
        var probe = await DatabaseProbe.RunAsync(ctx.Get<ISqlRunner>(), ctx.Get<IOptions<SterpDatabaseOptions>>(), ctx.Aborted);
        var ready = await HealthEndpoints.ReadinessAsync(ctx.Services, ctx.Aborted);
        var drift = (ready["checks"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(c => c["name"]?.GetValue<string>() == "schema-drift");
        var differences = ready["driftDifferences"] as JsonArray ?? [];
        var jobs = ctx.Get<JobService>();
        var engine = ctx.Get<IOptions<SterpHostOptions>>().Value.Engine;
        return ctx.Direct(new JsonObject
        {
            ["serviceName"] = engine == "batch" ? "STERP.Jobs" : "STERP.Api",
            ["serviceVersion"] = DatabaseProbe.ServiceVersion(),
            ["database"] = probe.ToJson(),
            ["status"] = probe.EngineStatus,
            ["readiness"] = ready["status"]?.GetValue<string>() ?? "Unhealthy",
            ["driftCheck"] = new JsonObject
            {
                ["status"] = drift is null ? "NOTRUN" : differences.Count == 0 ? "OK" : "DIFFERENT",
                ["differences"] = differences.DeepClone(),
            },
            ["deliveredSlices"] = new JsonArray(ContractCatalog.Waves
                .Where(w => !HostDocuments.Contains(w.Key))
                .OrderBy(w => int.Parse(w.Value[1..], System.Globalization.CultureInfo.InvariantCulture))
                .Select(w => (JsonNode?)new JsonObject
                {
                    ["targetApplicationId"] = w.Key,
                    ["wave"] = w.Value,
                    ["delivered"] = jobs.IsDelivered(w.Key),
                }).ToArray()),
            ["healthText"] = HealthText(probe),
        });
    }

    private static async Task<IResult> GetStatusAsync(OperationContext ctx)
    {
        string status;
        try
        {
            status = (await DatabaseProbe.RunAsync(ctx.Get<ISqlRunner>(), ctx.Get<IOptions<SterpDatabaseOptions>>(), ctx.Aborted)).EngineStatus;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = "DOWN";
        }
        return ctx.Direct(new JsonObject { ["status"] = status });
    }

    /// <summary>STERPOPS 7100-PING message: STERP-I-7000 when the probe is OK, else STERP-E-7001 + the first 60 characters.</summary>
    public static string HealthText(ProbeResult probe) => probe.IsOk
        ? "STERP-I-7000 ENGINE AND DATABASE HEALTHY"
        : "STERP-E-7001 " + Sterp.Platform.Kernel.Cobol.CobolText.Trimmed(probe.Message, 60);
}
