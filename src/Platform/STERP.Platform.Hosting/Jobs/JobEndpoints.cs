using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;

namespace Sterp.Platform.Hosting.Jobs;

/// <summary>HTTP surface of the job framework: slice submissions and the PLATFORM generic run resources.</summary>
public static class JobEndpoints
{
    public const string WindowsTextContentType = "text/plain; charset=windows-1252";

    /// <summary>Registers POST /api/{segment}/v1/jobs/{jobCode}/runs for every job definition that names its operation.</summary>
    public static void RegisterSliceSubmissions(OperationRegistry ops, JobCatalog catalog)
    {
        foreach (var d in catalog.All.Where(d => d.SubmitOperationId is not null))
        {
            var definition = d;
            ops.Handle(d.SubmitOperationId!, ctx => SubmitFromSliceAsync(ctx, definition));
        }
    }

    /// <summary>
    /// Slice submission (FR-STERP-PLATFORM-057 step 4): typed parameters in schema order through ReportService.BuildArguments,
    /// USER defaulting to the caller's STERP user (kept in place when present, appended last otherwise).
    /// </summary>
    public static async Task<IResult> SubmitFromSliceAsync(OperationContext ctx, JobDefinition d)
    {
        var body = await ctx.BodyObjectAsync();
        var cmd = BuildSliceCommandLine(body, d, ctx.Session.UserCode);
        var jobs = ctx.Get<JobService>();
        var accepted = await jobs.SubmitAsync(new JobSubmitRequest(JobEntryPoint.Slice, d, cmd, body), ctx.Session, ctx.Services, ctx.Aborted);
        return Accepted(ctx, accepted);
    }

    public static string BuildSliceCommandLine(JsonObject body, JobDefinition d, string caller)
    {
        var ordered = new List<KeyValuePair<string, string?>>();
        var keys = body.Select(p => p.Key).ToList();
        foreach (var k in d.ParameterOrder)
        {
            var hit = keys.FirstOrDefault(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
            if (hit is null) continue;
            ordered.Add(new(k, Text(body[hit])));
            keys.Remove(hit);
        }
        foreach (var k in keys) ordered.Add(new(k, Text(body[k])));
        if (d.DefaultUserToCaller)
        {
            var user = ordered.FindIndex(p => string.Equals(p.Key, "USER", StringComparison.OrdinalIgnoreCase));
            if (user < 0) ordered.Add(new("USER", caller));
            else if (string.IsNullOrWhiteSpace(ordered[user].Value)) ordered[user] = new("USER", caller);
        }
        return CommandLineArgs.Build(ordered);
    }

    private static string? Text(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Y" : "N",
        _ => n.ToJsonString(),
    };

    /// <summary>The 'parameters' array of a catalogue or console submission (PlatformJobParameter key/value pairs, in order).</summary>
    public static List<JobParameter> ParseParameters(JsonObject body)
    {
        if (body["parameters"] is not JsonArray items) throw ApiProblemException.BadBody("parameters is required.");
        var list = new List<JobParameter>();
        foreach (var item in items)
        {
            if (item is not JsonObject o) throw ApiProblemException.BadBody("Each parameter must be an object with key and value.");
            list.Add(new JobParameter(Text(o["key"]), Text(o["value"])));
        }
        return list;
    }

    public static IResult Accepted(OperationContext ctx, JobAccepted a)
    {
        var id = a.JobRunId.ToString(CultureInfo.InvariantCulture);
        var self = $"/api/jobs/v1/runs/{id}";
        return ctx.Accepted(new JsonObject
        {
            ["jobRunId"] = a.JobRunId,
            ["jobName"] = a.JobName,
            ["status"] = a.Status,
            ["startedAt"] = SqlJson.LocalDateTime(a.StartedAt),
            ["links"] = new JsonObject
            {
                ["self"] = self,
                ["sysout"] = self + "/sysout",
                ["spool"] = self + "/spool",
                ["result"] = self + "/result",
            },
        }, self);
    }

    /// <summary>The PLATFORM generic run resources (FR-STERP-PLATFORM-045, -046, -047, -048).</summary>
    public static void RegisterRunResources(OperationRegistry ops)
    {
        ops.Handle("jobs_listRuns", ListRunsAsync);
        ops.Handle("jobs_getRun", GetRunAsync);
        ops.Handle("jobs_getRunSysout", ctx => RunFileAsync(ctx, "LogFile"));
        ops.Handle("jobs_getRunSpool", ctx => RunFileAsync(ctx, "SpoolFile"));
        ops.Handle("jobs_getRunResult", GetResultAsync);
        ops.Handle("jobs_listSpoolFiles", ListSpoolFilesAsync);
        ops.Handle("jobs_getSpoolFile", GetSpoolFileAsync);
        ops.Handle("jobs_restartRun", RestartRunAsync);
    }

    /// <summary>jobs_restartRun (FR-STERP-PLATFORM-048): 202 for the new run of the stream, restarted at the label.</summary>
    private static async Task<IResult> RestartRunAsync(OperationContext ctx)
    {
        var id = RunId(ctx);
        var body = await ctx.BodyObjectAsync();
        var label = body["fromStep"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (label is null) throw ApiProblemException.BadBody("fromStep is required.");
        var accepted = await ctx.Get<JobService>().RestartAsync(id, label, ctx.Session, ctx.Services, ctx.Aborted);
        return Accepted(ctx, accepted);
    }

    private static async Task<IResult> ListRunsAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var status = ctx.Query("status");
        var rows = await sql.QueryJsonAsync("core.usp_UiJobList", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            FromDate = ctx.QueryDate("fromDate"),
            Status = status,
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    private const string RunSql =
        "SELECT JobRunId, JobName, CompanyCode, SiteCode, Arguments, SubmittedBy, StartedAt, EndedAt, Status, ReturnCode, RowsProcessed, LogFile, SpoolFile, Message FROM core.JobRun WHERE JobRunId = @JobRunId";

    private static async Task<IResult> GetRunAsync(OperationContext ctx)
    {
        var id = RunId(ctx);
        var jobs = ctx.Get<JobService>();
        var wait = ctx.Get<Microsoft.Extensions.Options.IOptions<SterpJobOptions>>().Value.RunPollWaitMilliseconds;
        await jobs.WaitForEndAsync(id, TimeSpan.FromMilliseconds(wait), ctx.Aborted);
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync(RunSql, new { JobRunId = id }, ctx.Aborted);
        if (rows.Count == 0) return ctx.NotFound("API0022", $"Job run {id} does not exist.", null);
        var run = (JsonObject)rows[0]!;
        var started = run["startedAt"] is JsonValue sv && sv.TryGetValue<string>(out var st) &&
                      DateTime.TryParse(st, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : (DateTime?)null;
        var m = started is null ? jobs.Manifests.Load(id) : jobs.LoadManifest(id, started.Value);
        run["execution"] = m is null ? null : Execution(m);
        var self = $"/api/jobs/v1/runs/{id}";
        run["links"] = new JsonObject
        {
            ["self"] = self,
            ["sysout"] = self + "/sysout",
            ["spool"] = self + "/spool",
            ["result"] = self + "/result",
            ["restart"] = self + "/restart",
        };
        return ctx.Direct(run);
    }

    public static JsonObject Execution(JobManifest m) => new()
    {
        ["entryPoint"] = m.EntryPoint,
        ["jobCode"] = m.JobCode,
        ["reportCode"] = m.ReportCode,
        ["programName"] = m.ProgramName,
        ["slice"] = m.Slice,
        ["commandLine"] = m.CommandLine,
        ["state"] = m.State,
        ["exitCode"] = m.ExitCode,
        ["acceptedAt"] = SqlJson.LocalDateTime(m.AcceptedAt),
        ["startedAt"] = m.StartedAt is { } s ? SqlJson.LocalDateTime(s) : null,
        ["endedAt"] = m.EndedAt is { } e ? SqlJson.LocalDateTime(e) : null,
        ["relatedJobRunIds"] = new JsonArray(m.RelatedJobRunIds.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        ["restartOfJobRunId"] = m.RestartOfJobRunId,
        ["steps"] = new JsonArray(m.Steps.Select(s => (JsonNode?)new JsonObject
        {
            ["stepName"] = s.StepName,
            ["program"] = s.Program,
            ["parm"] = s.Parm,
            ["condCode"] = s.CondCode,
            ["maxCondCode"] = s.MaxCondCode,
        }).ToArray()),
        ["spoolFiles"] = new JsonArray(m.SpoolFiles.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        ["hasResult"] = m.HasResult,
    };

    private static async Task<IResult> RunFileAsync(OperationContext ctx, string column)
    {
        var id = RunId(ctx);
        var sql = ctx.Get<IProcedureRunner>();
        var path = await sql.QueryFirstOrDefaultAsync<string?>($"SELECT {column} FROM core.JobRun WHERE JobRunId = @JobRunId", new { JobRunId = id }, ctx.Aborted);
        if (string.IsNullOrWhiteSpace(path)) return ctx.NotFound("API0022", $"Job run {id} has no {(column == "LogFile" ? "SYSOUT" : "spool")} file.", null);
        return ServeFile(ctx, path.Trim(), "API0022");
    }

    public static IResult ServeFile(OperationContext ctx, string path, string notFoundReason)
    {
        var jobs = ctx.Get<JobService>();
        if (!jobs.Paths.IsUnderRoots(path, out _))
        {
            return ctx.Forbidden("API0021", $"{path} is outside the configured output roots.");
        }
        if (!File.Exists(path))
        {
            return notFoundReason == "SPL0001"
                ? ctx.NotFound("SPL0001", $"Spool file {path} no longer exists.", null)
                : ctx.NotFound(notFoundReason, $"{path} no longer exists.", null);
        }
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        return Results.Bytes(bytes, WindowsTextContentType);
    }

    private static Task<IResult> GetResultAsync(OperationContext ctx)
    {
        var id = RunId(ctx);
        var result = ctx.Get<JobService>().Manifests.LoadResult(id);
        return Task.FromResult(result is null
            ? ctx.NotFound("API0022", $"Job run {id} has no stored result.", null)
            : ctx.Direct(result));
    }

    private static Task<IResult> ListSpoolFilesAsync(OperationContext ctx)
    {
        var filter = (ctx.Query("filter") ?? "lst").Trim().ToLowerInvariant();
        var folder = (ctx.Query("folder") ?? "spool").Trim().ToLowerInvariant();
        var jobs = ctx.Get<JobService>();
        var items = new List<(DateTime Modified, JsonObject Row)>();
        foreach (var (root, baseDir) in new[] { ("target", jobs.Paths.Base), ("legacy", jobs.Paths.LegacyBase) })
        {
            var dir = Path.Combine(baseDir, folder);
            if (!Directory.Exists(dir)) continue;
            var pattern = filter switch { "lst" => "*.lst", "log" => "*.log", _ => "*" };
            foreach (var f in new DirectoryInfo(dir).GetFiles(pattern))
            {
                items.Add((f.LastWriteTime, new JsonObject
                {
                    ["path"] = f.FullName,
                    ["fileName"] = f.Name,
                    ["folder"] = folder,
                    ["root"] = root,
                    ["sizeBytes"] = f.Length,
                    ["modifiedAt"] = SqlJson.LocalDateTime(f.LastWriteTime),
                }));
            }
        }
        var arr = new JsonArray(items.OrderByDescending(i => i.Modified).Select(i => (JsonNode?)i.Row).ToArray());
        return Task.FromResult(ctx.List(arr));
    }

    private static Task<IResult> GetSpoolFileAsync(OperationContext ctx)
    {
        var path = ctx.Query("path");
        if (string.IsNullOrWhiteSpace(path)) throw ApiProblemException.BadBody("Query parameter path is required.");
        return Task.FromResult(ServeFile(ctx, path.Trim(), "SPL0001"));
    }

    private static long RunId(OperationContext ctx) =>
        long.TryParse(ctx.Route("jobRunId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw ApiProblemException.BadBody("jobRunId must be an integer.");
}
