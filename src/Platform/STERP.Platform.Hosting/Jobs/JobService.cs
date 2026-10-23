using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;

namespace Sterp.Platform.Hosting.Jobs;

/// <summary>A submission through one of the legacy entry points (FR-STERP-PLATFORM-057).</summary>
public sealed record JobSubmitRequest(
    JobEntryPoint EntryPoint,
    JobDefinition Definition,
    string CommandLine,
    JsonObject Parameters,
    string? ReportCode = null,
    JobRegistration? LauncherRow = null,
    long? RestartOf = null)
{
    /// <summary>Entry point whose rules apply (a restart re-submits through the original entry point); default the entry point.</summary>
    public JobEntryPoint? SubmitRules { get; init; }

    /// <summary>Operator console page-open date (OPS log stamp, STERPOPS 3350).</summary>
    public DateOnly? SessionStartDate { get; init; }

    public JobEntryPoint Rules => SubmitRules ?? EntryPoint;
}

/// <summary>202 JobRunAccepted.</summary>
public sealed record JobAccepted(long JobRunId, string JobName, string Status, DateTime StartedAt);

/// <summary>core.usp_JobRunStart result row.</summary>
public sealed record JobRunStarted(long JobRunId, string JobName, string Status, DateTime StartedAt);

/// <summary>One catalogue or console panel entry (key and value as entered).</summary>
public sealed record JobParameter(string? Key, string? Value);

/// <summary>
/// IJobSubmission + job host (STERP-JOBS FR-010, PLATFORM FR-044, -048, -049, -051, -057): validates delivery and the
/// pre-registration checks, registers exactly as the legacy entry point did, writes the run manifest, returns 202 and executes
/// the run on its own worker (no queue limit, no time-out, no retry); ends the registration(s) with the legacy END values.
/// Entry points: slice POST (<see cref="JobEndpoints.SubmitFromSliceAsync"/>), catalogue launch
/// (<see cref="LaunchFromCatalogueAsync"/>), operator console (<see cref="SubmitFromConsoleAsync"/>) and restart
/// (<see cref="RestartAsync"/>); stream steps run through <see cref="RunStepProgramAsync"/>.
/// </summary>
public sealed class JobService(
    IServiceScopeFactory scopes,
    JobCatalog catalog,
    OperationRegistry registry,
    OutputPaths paths,
    JobManifestStore manifests,
    ISqlConnectionFactory connections,
    IOptions<SterpJobOptions> options,
    IOptions<SterpHostOptions> hostOptions,
    ILogger<JobService> logger)
{
    public const string NotInCatalogue = "API0020";
    public const string NotRestartable = "API0023";
    public const string UnknownRestartLabel = "API0024";
    public const string RunNotFound = "API0022";

    private readonly ConcurrentDictionary<long, Task> _active = new();
    private readonly SemaphoreSlim? _limit = options.Value.MaxConcurrentRuns > 0 ? new SemaphoreSlim(options.Value.MaxConcurrentRuns) : null;

    public int ActiveRuns => _active.Count;

    public JobCatalog Catalog => catalog;

    public OutputPaths Paths => paths;

    public JobManifestStore Manifests => manifests;

    public bool IsDelivered(string slice) =>
        registry.IsDelivered(slice) && !hostOptions.Value.UndeliveredSlices.Contains(slice, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ delivery (contracts 8.4, D-PLAT-07)

    private sealed record DeliveryGap(string? Slice, string Program, string? Step);

    /// <summary>The first undelivered slice a job needs, or null (contracts 8.4).</summary>
    public string? UndeliveredSliceOf(JobDefinition d, out string program)
    {
        var gap = FindGap(d);
        program = gap?.Program ?? d.Program;
        return gap is null ? null : gap.Slice ?? "UNKNOWN";
    }

    private DeliveryGap? FindGap(JobDefinition d)
    {
        if (!IsDelivered(d.Slice)) return new DeliveryGap(d.Slice, d.Program, null);
        if (d.Stream is not null)
        {
            foreach (var step in d.Stream.ProgramSteps)
            {
                var program = catalog.FindProgram(step.Program);
                if (program is null) return new DeliveryGap(JobOwnership.SliceOf(step.Program), step.Program, step.StepName);
                if (!IsDelivered(program.Slice)) return new DeliveryGap(program.Slice, step.Program, step.StepName);
            }
        }
        foreach (var (step, prog, slice) in d.StepPrograms)
        {
            if (!IsDelivered(slice)) return new DeliveryGap(slice, prog, step);
        }
        return null;
    }

    /// <summary>501 API0501 before anything is registered when the program or a stream step is not delivered (D-PLAT-07).</summary>
    public void EnsureDelivered(JobDefinition d)
    {
        var gap = FindGap(d);
        if (gap is null) return;
        var what = d.IsStream ? $"Job stream {d.Program}" : $"Job {d.JobCode}";
        var step = gap.Step is null ? "" : $" step {gap.Step}";
        throw ApiProblemException.NotDelivered(Missing($"{what}{step} needs program {gap.Program}", gap.Slice, gap.Slice is not null && !IsDelivered(gap.Slice)));
    }

    /// <summary>
    /// Resolves the program or stream a catalogue or console entry runs (ReportService.Run: JobStream or bin\x86\PROG.exe);
    /// 501 API0501 naming the owning slice when no delivered module provides it.
    /// </summary>
    public JobDefinition ResolveEntryTarget(string programName, bool stream, string? module)
    {
        var prog = programName.Trim().ToUpperInvariant();
        var d = stream ? catalog.FindStream(prog) : catalog.FindProgram(prog);
        if (d is null)
        {
            var slice = JobOwnership.SliceOf(prog, module);
            var what = stream ? $"Job stream {prog}" : $"Batch program {prog}";
            throw ApiProblemException.NotDelivered(Missing(what, slice, slice is not null && !IsDelivered(slice)));
        }
        EnsureDelivered(d);
        return d;
    }

    private static string Missing(string what, string? slice, bool undelivered)
    {
        if (slice is null) return $"{what}, which no slice of the modernized application provides - use the legacy client.";
        var wave = Contracts.ContractCatalog.Waves.GetValueOrDefault(slice, "");
        return undelivered
            ? $"{what} of slice {slice} (wave {wave}), which is not yet delivered in the modernized application - use the legacy client."
            : $"{what}, which slice {slice} (wave {wave}) does not provide in the modernized application - use the legacy client.";
    }

    // ------------------------------------------------------------------ entry points

    /// <summary>
    /// Catalogue launch (FR-STERP-PLATFORM-044; frmReportLauncher.RunClicked + ReportService.Run): non-empty trimmed panel
    /// values keyed by the upper-cased panel key (a repeated key keeps its first position), USER replaced in place by the caller
    /// or appended last, ReportService.BuildArguments; 'JOB.' (trimmed, any case) runs the stream; the launcher row
    /// (ProgramName, session company, user) is registered first and its id is the 202 JobRunId (SME-010).
    /// </summary>
    public Task<JobAccepted> LaunchFromCatalogueAsync(string reportCode, string programName, string? module, IEnumerable<JobParameter> parameters,
        SterpContext session, IServiceProvider services, CancellationToken ct)
    {
        var stream = reportCode.Trim().StartsWith("JOB.", StringComparison.OrdinalIgnoreCase);
        var d = ResolveEntryTarget(programName, stream, module);
        var cmd = CatalogueCommandLine(parameters, session.UserCode);
        var launcher = new JobRegistration(programName.Trim().ToUpperInvariant(), session.CompanyCode, session.UserCode, null, null);
        return SubmitAsync(new JobSubmitRequest(JobEntryPoint.Catalogue, d, cmd, new JsonObject(), reportCode.Trim(), launcher), session, services, ct);
    }

    /// <summary>frmReportLauncher.RunClicked argument dictionary + ReportService.BuildArguments.</summary>
    public static string CatalogueCommandLine(IEnumerable<JobParameter> parameters, string caller)
    {
        var args = new List<KeyValuePair<string, string?>>();
        foreach (var p in parameters)
        {
            var key = (p.Key ?? "").Trim().ToUpperInvariant();
            var value = (p.Value ?? "").Trim();
            if (key.Length == 0 || value.Length == 0) continue;
            var at = args.FindIndex(a => string.Equals(a.Key, key, StringComparison.Ordinal));
            if (at >= 0) args[at] = new(key, value);
            else args.Add(new(key, value));
        }
        var user = args.FindIndex(a => string.Equals(a.Key, "USER", StringComparison.Ordinal));
        if (user >= 0) args[user] = new("USER", caller);
        else args.Add(new("USER", caller));
        return CommandLineArgs.Build(args);
    }

    /// <summary>
    /// Operator console submission (FR-STERP-PLATFORM-051; STERPOPS 3200/3300/3350): ' KEY=VALUE' for every non-blank key (14)
    /// and value (24), unquoted, at most 200 characters; ReportCode starting exactly 'JOB.' runs the stream; the console
    /// registers nothing itself (the 202 JobRunId is the job's own registration); no catalogue permission applies.
    /// </summary>
    public Task<JobAccepted> SubmitFromConsoleAsync(string reportCode, string programName, string? module, IEnumerable<JobParameter> parameters,
        DateOnly sessionStartDate, SterpContext session, IServiceProvider services, CancellationToken ct)
    {
        var stream = reportCode.StartsWith("JOB.", StringComparison.Ordinal);
        var prog = CobolText.Trim(CobolText.Fit(programName, 8));
        var d = ResolveEntryTarget(prog, stream, module);
        var cmd = ConsoleCommandLine(parameters);
        return SubmitAsync(new JobSubmitRequest(JobEntryPoint.Console, d, cmd, new JsonObject(), reportCode.Trim())
        {
            SessionStartDate = sessionStartDate,
        }, session, services, ct);
    }

    /// <summary>STERPOPS 3350-BUILD-COMMAND: STRING TRIM(WS-TEXT) ' ' TRIM(key) '=' TRIM(value) into X(200), then TRIM.</summary>
    public static string ConsoleCommandLine(IEnumerable<JobParameter> parameters)
    {
        var text = "";
        foreach (var p in parameters)
        {
            var key = CobolText.Trim(CobolText.Fit(p.Key, 14));
            var value = CobolText.Trim(CobolText.Fit(p.Value, 24));
            if (key.Length == 0 || value.Length == 0) continue;
            text = text.Trim() + " " + key + "=" + value;
            if (text.Length > 200) text = text[..200];
        }
        return text.Trim();
    }

    /// <summary>
    /// jobs_restartRun (FR-STERP-PLATFORM-048; STERP-PLATFORM 4.11): a stream run with a target manifest (else 422 API0023), a
    /// label that is a STEPnnn label, ABEND or EOJ of the stream (else 422 API0024); the original command line plus
    /// ' RESTART=&lt;label&gt;' re-submitted through the original entry point's rules and checks; a new run is registered.
    /// </summary>
    public async Task<JobAccepted> RestartAsync(long jobRunId, string label, SterpContext session, IServiceProvider services, CancellationToken ct)
    {
        var sql = services.GetRequiredService<IProcedureRunner>();
        var started = await sql.QueryFirstOrDefaultAsync<DateTime?>(
            "SELECT StartedAt FROM core.JobRun WHERE JobRunId = @JobRunId", new { JobRunId = jobRunId }, ct);
        if (started is null) throw new ApiProblemException(404, ProblemKinds.NotFound, RunNotFound, $"Job run {jobRunId} does not exist.");
        var m = LoadManifest(jobRunId, started.Value);
        if (m is null || !m.IsStream || m.EntryPoint == JobEntryPoint.StreamStep.ManifestName())
        {
            throw new ApiProblemException(422, ProblemKinds.BusinessRule, NotRestartable,
                $"Job run {jobRunId} was not executed as a job stream by the modernized job host; its parameters cannot be rebuilt - resubmit it with RESTART=<label>.");
        }
        var d = catalog.Find(m.JobCode) is { IsStream: true } byCode ? byCode : catalog.FindStream(m.ProgramName);
        if (d?.Stream is null)
        {
            var slice = JobOwnership.SliceOf(m.ProgramName);
            throw ApiProblemException.NotDelivered(Missing($"Job stream {m.ProgramName}", slice, slice is not null && !IsDelivered(slice)));
        }
        var step = (label ?? "").Trim();
        if (step.Length == 0 || !d.Stream.IsRestartLabel(step))
        {
            throw new ApiProblemException(422, ProblemKinds.BusinessRule, UnknownRestartLabel,
                $"{(step.Length == 0 ? "(blank)" : step)} is not a STEPnnn label, ABEND or EOJ of job stream {d.Program}.");
        }
        EnsureDelivered(d);
        var rules = JobEntryPoints.Parse(m.SubmitRules);
        JobRegistration? launcher = null;
        if (rules == JobEntryPoint.Catalogue)
        {
            if (!await InReportCatalogueAsync(sql, session, m.ReportCode, ct))
            {
                throw ApiProblemException.Forbidden(NotInCatalogue, $"Report {m.ReportCode} is not in the report catalogue of {session.UserCode}.");
            }
            launcher = new JobRegistration(m.ProgramName.Trim().ToUpperInvariant(), session.CompanyCode, session.UserCode, null, null);
        }
        var cmd = (m.CommandLine + " RESTART=" + step).TrimStart();
        return await SubmitAsync(new JobSubmitRequest(JobEntryPoint.Restart, d, cmd, new JsonObject(), m.ReportCode, launcher, jobRunId)
        {
            SubmitRules = rules,
            SessionStartDate = m.SessionStartDate,
        }, session, services, ct);
    }

    private static async Task<bool> InReportCatalogueAsync(IProcedureRunner sql, SterpContext s, string? reportCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reportCode)) return false;
        var rows = await sql.QueryJsonAsync("core.usp_UiReportList", new Dictionary<string, object?>
        {
            ["@UserCode"] = s.UserCode,
            ["@CompanyCode"] = s.CompanyCode,
            ["@Module"] = null,
        }, ct);
        return rows.OfType<JsonObject>().Any(r =>
            r["reportCode"] is JsonValue v && v.TryGetValue<string>(out var code) &&
            string.Equals(code.Trim(), reportCode.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ submission lifecycle (STERP-PLATFORM 4.9)

    private sealed class RunJob
    {
        public required long RunId { get; init; }
        public required JobSubmitRequest Request { get; init; }
        public required JobManifest Manifest { get; init; }
        public required SterpContextSnapshot Snapshot { get; init; }
        public required List<long> ManifestIds { get; init; }
        public long? LauncherId { get; init; }
        public long? OwnId { get; init; }
        public bool SelfRegistered { get; init; }
        public StreamPrologue? Prologue { get; init; }
    }

    public async Task<JobAccepted> SubmitAsync(JobSubmitRequest req, SterpContext session, IServiceProvider services, CancellationToken ct)
    {
        var d = req.Definition;
        EnsureDelivered(d);

        var pre = new JobPreCheckContext(d, req.CommandLine, session, services, req.Parameters);
        if (d.PreCheck is not null)
        {
            var refusal = await d.PreCheck(pre);
            if (refusal is { IsFailed: true }) throw new LegacyOutcomeException(refusal);
        }

        var sql = services.GetRequiredService<IProcedureRunner>();
        var now = DateTime.Now;
        JobRunStarted? launcher = null;
        JobRunStarted? own = null;
        JobRegistration? self = null;
        StreamPrologue? prologue = null;
        var rows = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            if (req.LauncherRow is not null)
            {
                launcher = await StartRowAsync(sql, req.LauncherRow, ct);
                rows[Key(launcher.JobRunId)] = await RowStartedAsync(sql, launcher.JobRunId, ct);
            }
            if (d.IsStream)
            {
                prologue = StreamPrologue.Parse(d.Stream!.Name, req.CommandLine, now);
                own = await StartStreamAsync(prologue, ct);
            }
            else
            {
                self = d.SelfRegistration?.Invoke(pre);
                if (self is not null)
                {
                    own = await StartRowAsync(sql, self, ct);
                }
                else if (launcher is null)
                {
                    // D-PLAT-09: a program that never registered itself gets one row named after its job code
                    own = await StartRowAsync(sql, new JobRegistration(d.JobCode, Blank(session.CompanyCode), session.UserCode,
                        Blank(session.SiteCode), Clip(req.CommandLine, 300)), ct);
                }
            }
            if (own is not null) rows[Key(own.JobRunId)] = await RowStartedAsync(sql, own.JobRunId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ApiProblemException)
        {
            // D-PLAT-05: the registration that precedes the 202 failed - nothing runs
            logger.LogWarning(ex, "JobRunStart failed for {Job}", d.JobCode);
            throw new ApiProblemException(500, ProblemKinds.Severe, "SYS0001", ex is SqlioException sx ? sx.DriverText : ex.Message);
        }

        var accepted = launcher ?? own ?? throw new InvalidOperationException($"Job {d.JobCode} registered no run.");
        var manifest = new JobManifest
        {
            EntryPoint = req.EntryPoint.ManifestName(),
            SubmitRules = req.Rules.ManifestName(),
            IsStream = d.IsStream,
            JobCode = d.JobCode,
            ReportCode = req.ReportCode,
            ProgramName = d.Program,
            Slice = d.Slice,
            CommandLine = req.CommandLine,
            State = "ACCEPTED",
            AcceptedAt = now,
            RestartOfJobRunId = req.RestartOf,
            LauncherJobRunId = launcher?.JobRunId,
            OwnJobRunId = own?.JobRunId,
            SessionStartDate = req.SessionStartDate,
            Rows = rows,
        };
        var ids = new List<long>();
        if (launcher is not null) ids.Add(launcher.JobRunId);
        if (own is not null && own.JobRunId != launcher?.JobRunId) ids.Add(own.JobRunId);
        manifest.RelatedJobRunIds.AddRange(ids);
        manifests.Save(ids, manifest);

        var job = new RunJob
        {
            RunId = accepted.JobRunId,
            Request = req,
            Manifest = manifest,
            Snapshot = session.Snapshot(),
            ManifestIds = ids,
            LauncherId = launcher?.JobRunId,
            OwnId = own?.JobRunId,
            SelfRegistered = self is not null,
            Prologue = prologue,
        };
        var task = Task.Run(() => ExecuteAsync(job));
        _active[job.RunId] = task;
        _ = task.ContinueWith(_ => _active.TryRemove(job.RunId, out var _), TaskScheduler.Default);
        return new JobAccepted(accepted.JobRunId, accepted.JobName, "RUNNING", rows.TryGetValue(Key(accepted.JobRunId), out var at) ? at : now);
    }

    private async Task ExecuteAsync(RunJob job)
    {
        if (_limit is not null) await _limit.WaitAsync();
        var m = job.Manifest;
        void Save() => manifests.Save(job.ManifestIds, m);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var session = services.GetRequiredService<SterpContext>();
            session.Restore(job.Snapshot);
            var d = job.Request.Definition;
            var rules = job.Request.Rules;
            using var console = OpenConsole(job, d, rules);
            m.State = "RUNNING";
            m.StartedAt = DateTime.Now;
            m.ConsoleFile = console.Path.Length > 0 ? console.Path : null;
            Save();

            JobRunContext? ctx = null;
            int exit;
            try
            {
                if (d.IsStream)
                {
                    var engine = new StreamEngine(this, d, m, Save, job.Snapshot, connections);
                    exit = await engine.RunAsync(job.Prologue!, job.OwnId, console, services, CancellationToken.None);
                }
                else
                {
                    ctx = new JobRunContext(job.RunId, d, job.Request.CommandLine, session, services, console, paths, m, CancellationToken.None)
                    {
                        ProgramRunId = job.OwnId,
                    };
                    exit = await d.Execute!(ctx);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {Job} run {RunId} failed", d.JobCode, job.RunId);
                var text = ex is SqlioException sx ? sx.DriverText : ex.Message;
                console.WriteLine("STERP-E-0012 " + text);
                m.Error = text;
                exit = 12;
            }

            var sqlRunner = services.GetRequiredService<IProcedureRunner>();
            // ReportService.Run: the launcher log is named when the program has ended
            var launcherLog = job.LauncherId is not null
                ? Path.Combine(paths.Folder("logs"), d.Program.Trim().ToUpperInvariant() + "-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".log")
                : null;
            var consoleLog = launcherLog ?? (console.Path.Length > 0 ? console.Path : null);
            if (ctx is not null && job.OwnId is not null)
            {
                var end = ctx.EndOverride ?? d.EndValues?.Invoke(ctx, exit) ?? DefaultEnd(ctx, exit, job.SelfRegistered, consoleLog);
                if (end.Status is not null) await TryEndAsync(sqlRunner, job.OwnId.Value, end with { LogFile = end.LogFile ?? consoleLog });
            }
            if (launcherLog is not null)
            {
                try
                {
                    File.WriteAllText(launcherLog, console.Text);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Report log: {Message}", ex.Message);
                }
                m.ConsoleFile = launcherLog;
                await TryEndAsync(sqlRunner, job.LauncherId!.Value, new JobEnd(LauncherStatus(exit), exit, null, launcherLog, Blank(ParseSpool(console.Text))));
            }
            if (ctx is not null)
            {
                if (ctx.Result is not null)
                {
                    foreach (var id in job.ManifestIds) manifests.SaveResult(id, ctx.Result);
                    m.HasResult = true;
                }
                m.SpoolFiles = ctx.SpoolFiles.ToList();
            }
            m.ExitCode = exit;
            m.EndedAt = DateTime.Now;
            m.State = "ENDED";
            Save();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job host failure for run {RunId}", job.RunId);
            m.State = "ENDED";
            m.Error = ex.Message;
            m.ExitCode ??= 12;
            m.EndedAt = DateTime.Now;
            Save();
        }
        finally
        {
            _limit?.Release();
        }
    }

    /// <summary>
    /// The run console: the catalogue launcher keeps it in memory and writes its log at the end; a console-submitted program
    /// writes run\logs\OPS-&lt;PROG&gt;-&lt;session stamp&gt;.log (overwritten, STERPOPS 3350); a slice-submitted program
    /// &lt;root&gt;\logs\&lt;PROG&gt;-&lt;jobRunId&gt;.log; a stream console (not kept by legacy) &lt;root&gt;\work\jobs\&lt;jobRunId&gt;.console.log.
    /// </summary>
    private JobConsole OpenConsole(RunJob job, JobDefinition d, JobEntryPoint rules)
    {
        if (job.LauncherId is not null) return new JobConsole();
        var id = job.RunId.ToString(CultureInfo.InvariantCulture);
        if (d.IsStream) return new JobConsole(Path.Combine(paths.Folder(Path.Combine("work", "jobs")), id + ".console.log"));
        if (rules == JobEntryPoint.Console)
        {
            var stamp = (job.Request.SessionStartDate ?? DateOnly.FromDateTime(DateTime.Today)).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "000000";
            return new JobConsole(Path.Combine(paths.Folder("logs"), "OPS-" + d.Program.Trim() + "-" + stamp + ".log"));
        }
        return new JobConsole(Path.Combine(paths.Folder("logs"), d.Program + "-" + id + ".log"));
    }

    /// <summary>
    /// RUNSTEP program step: the delivered program runs in-process on a fresh scope with PARM as its command line and its
    /// console appended to SYSOUT; it registers itself as it always did (no D-PLAT-09 row inside a stream) and gets a
    /// STREAM-STEP manifest under its own run id. Returns the exit code (LASTCC) and the program's own run id.
    /// </summary>
    internal async Task<(int Exit, long? OwnRunId)> RunStepProgramAsync(JobDefinition d, string parm, JobConsole sysout,
        SterpContextSnapshot snapshot, long? streamRunId, JobManifest streamManifest, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var session = services.GetRequiredService<SterpContext>();
        session.Restore(snapshot);
        var sql = services.GetRequiredService<IProcedureRunner>();
        var pre = new JobPreCheckContext(d, parm, session, services, new JsonObject());
        try
        {
            if (d.PreCheck is not null)
            {
                var refusal = await d.PreCheck(pre);
                if (refusal is { IsFailed: true })
                {
                    foreach (var line in d.RefusalDisplay?.Invoke(refusal) ?? [refusal.Message]) sysout.WriteLine(line);
                    return (refusal.ReturnCode, null);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sysout.WriteLine("STERP-E-0012 " + (ex is SqlioException sx ? sx.DriverText : ex.Message));
            return (12, null);
        }

        long? ownId = null;
        var now = DateTime.Now;
        var m = new JobManifest
        {
            EntryPoint = JobEntryPoint.StreamStep.ManifestName(),
            SubmitRules = JobEntryPoint.StreamStep.ManifestName(),
            JobCode = d.JobCode,
            ProgramName = d.Program,
            Slice = d.Slice,
            CommandLine = parm,
            State = "RUNNING",
            AcceptedAt = now,
            StartedAt = now,
            StreamJobRunId = streamRunId,
            ConsoleFile = sysout.Path,
        };
        try
        {
            var self = d.SelfRegistration?.Invoke(pre);
            if (self is not null)
            {
                ownId = (await StartRowAsync(sql, self, ct)).JobRunId;
                m.Rows[Key(ownId.Value)] = await RowStartedAsync(sql, ownId.Value, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("JobRunStart: {Message}", ex.Message);
        }
        if (ownId is not null)
        {
            m.OwnJobRunId = ownId;
            m.RelatedJobRunIds.Add(ownId.Value);
            if (streamRunId is not null) m.RelatedJobRunIds.Add(streamRunId.Value);
            manifests.Save(ownId.Value, m);
        }

        var ctx = new JobRunContext(ownId ?? streamRunId ?? 0, d, parm, session, services, sysout, paths, m, ct) { ProgramRunId = ownId };
        int exit;
        try
        {
            exit = await d.Execute!(ctx);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Stream step program {Program} failed", d.Program);
            var text = ex is SqlioException sx ? sx.DriverText : ex.Message;
            sysout.WriteLine("STERP-E-0012 " + text);
            m.Error = text;
            exit = 12;
        }
        if (ownId is not null)
        {
            var end = ctx.EndOverride ?? d.EndValues?.Invoke(ctx, exit) ?? DefaultEnd(ctx, exit, true, sysout.Path);
            if (end.Status is not null) await TryEndAsync(sql, ownId.Value, end with { LogFile = end.LogFile ?? sysout.Path });
            if (ctx.Result is not null)
            {
                manifests.SaveResult(ownId.Value, ctx.Result);
                m.HasResult = true;
            }
            m.SpoolFiles = ctx.SpoolFiles.ToList();
            m.ExitCode = exit;
            m.EndedAt = DateTime.Now;
            m.State = "ENDED";
            manifests.Save(ownId.Value, m);
        }
        foreach (var spool in ctx.SpoolFiles.Where(s => !streamManifest.SpoolFiles.Contains(s))) streamManifest.SpoolFiles.Add(spool);
        return (exit, ownId);
    }

    /// <summary>
    /// SETENV registration (SETENV.cmd:99): core.usp_JobRunStart @JobName = stream, @CompanyCode = CO, @SubmittedBy = USER,
    /// @Arguments = 'MODE=.. ASAT=.. YEAR=.. PERIOD=..' with the values exactly as sqlcmd passed them (no ''-to-NULL rule).
    /// </summary>
    private async Task<JobRunStarted> StartStreamAsync(StreamPrologue p, CancellationToken ct)
    {
        await using var cn = connections.Create();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("core.usp_JobRunStart", cn) { CommandType = System.Data.CommandType.StoredProcedure };
        cmd.Parameters.Add(new SqlParameter("@JobName", System.Data.SqlDbType.VarChar, 40) { Value = p.JobName });
        cmd.Parameters.Add(new SqlParameter("@CompanyCode", System.Data.SqlDbType.VarChar, 10) { Value = p.Company });
        cmd.Parameters.Add(new SqlParameter("@SubmittedBy", System.Data.SqlDbType.VarChar, 20) { Value = p.User });
        cmd.Parameters.Add(new SqlParameter("@Arguments", System.Data.SqlDbType.VarChar, 300) { Value = p.Arguments });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("core.usp_JobRunStart returned no run id");
        return new JobRunStarted(Convert.ToInt64(r["JobRunId"], CultureInfo.InvariantCulture), Convert.ToString(r["JobName"], CultureInfo.InvariantCulture) ?? p.JobName,
            Convert.ToString(r["Status"], CultureInfo.InvariantCulture) ?? "RUNNING", Convert.ToDateTime(r["StartedAt"], CultureInfo.InvariantCulture));
    }

    private static async Task<DateTime> RowStartedAsync(IProcedureRunner sql, long id, CancellationToken ct) =>
        await sql.QueryFirstOrDefaultAsync<DateTime?>("SELECT StartedAt FROM core.JobRun WHERE JobRunId = @JobRunId", new { JobRunId = id }, ct)
        ?? DateTime.MinValue;

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The run manifest of a core.JobRun row, or null when there is none or it belongs to an earlier row with the same id (a
    /// restored database reuses ids while the output root keeps the manifests).
    /// </summary>
    public JobManifest? LoadManifest(long id, DateTime rowStartedAt)
    {
        var m = manifests.Load(id);
        if (m is null) return null;
        return m.Rows.TryGetValue(Key(id), out var stamp) && stamp != rowStartedAt ? null : m;
    }

    private async Task TryEndAsync(IProcedureRunner sql, long id, JobEnd end)
    {
        try
        {
            await EndAsync(sql, id, end);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("JobRunEnd: {Message}", ex.Message);
        }
    }

    internal void LogWarning(string message) => logger.LogWarning("{Message}", message);

    // ------------------------------------------------------------------ END mappings

    /// <summary>
    /// Default END of a program's own row: JOBLOG END as the FIN/XMD suites map it (0 COMPLETE->OK, 4 WARNING, other FAILED)
    /// for a self-registering program; the JOBEND mapping for the D-PLAT-09 job-code row.
    /// </summary>
    public static JobEnd DefaultEnd(JobRunContext ctx, int exit, bool selfRegistered, string? logPath)
    {
        var status = selfRegistered
            ? exit switch { 0 => "OK", 4 => "WARNING", _ => "FAILED" }
            : JobendStatus(exit);
        return new JobEnd(status, exit, ctx.RowsProcessed, logPath, Blank(ctx.SpoolFile), Blank(ctx.Message));
    }

    /// <summary>ReportService.JobStatus (ReportService.vb:188-193): &lt;=0 OK, &lt;=4 WARNING, &lt;16 FAILED, else ABENDED.</summary>
    public static string LauncherStatus(int rc) => rc <= 0 ? "OK" : rc <= 4 ? "WARNING" : rc < 16 ? "FAILED" : "ABENDED";

    /// <summary>JOBEND.cmd:15-18: &gt;= 12 ABENDED, &gt;= 8 FAILED, &gt;= 4 WARNING, else OK.</summary>
    public static string JobendStatus(int cc) => cc >= 12 ? "ABENDED" : cc >= 8 ? "FAILED" : cc >= 4 ? "WARNING" : "OK";

    /// <summary>ReportService.ParseSpool: path of the last console line 'STERP-I-SPOOL' + white space (case-insensitive).</summary>
    public static string ParseSpool(string? console)
    {
        var found = "";
        if (console is null) return found;
        foreach (var line in console.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 13 && t.StartsWith("STERP-I-SPOOL", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(t[13]))
            {
                var p = t[13..].Trim();
                if (p.Length > 0) found = p;
            }
        }
        return found;
    }

    public static async Task<long> StartAsync(IProcedureRunner sql, JobRegistration r, CancellationToken ct) =>
        (await StartRowAsync(sql, r, ct)).JobRunId;

    public static async Task<JobRunStarted> StartRowAsync(IProcedureRunner sql, JobRegistration r, CancellationToken ct)
    {
        var row = await sql.QueryFirstOrDefaultAsync<JobRunStarted>("core.usp_JobRunStart", new
        {
            r.JobName,
            CompanyCode = r.CompanyCode,
            SubmittedBy = r.SubmittedBy,
            SiteCode = r.SiteCode,
            Arguments = r.Arguments,
        }, ct);
        return row ?? throw new InvalidOperationException("core.usp_JobRunStart returned no run id");
    }

    public static Task EndAsync(IProcedureRunner sql, long id, JobEnd e) =>
        sql.ExecuteAsync("core.usp_JobRunEnd", new
        {
            JobRunId = id,
            e.Status,
            e.ReturnCode,
            e.RowsProcessed,
            LogFile = Clip(e.LogFile, 260),
            SpoolFile = Clip(e.SpoolFile, 260),
            Message = Clip(e.Message, 400),
        });

    public static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string? Clip(string? s, int n) => s is null ? null : s.Length > n ? s[..n] : s;

    /// <summary>Waits for every run in flight (tests and orderly shutdown).</summary>
    public Task DrainAsync() => Task.WhenAll(_active.Values);

    /// <summary>Waits at most <paramref name="budget"/> for a run executing in this host to end; returns at once for any other run.</summary>
    public async Task WaitForEndAsync(long runId, TimeSpan budget, CancellationToken ct)
    {
        if (budget <= TimeSpan.Zero || !_active.TryGetValue(runId, out var task)) return;
        try
        {
            await task.WaitAsync(budget, ct);
        }
        catch (TimeoutException)
        {
            // still running: the caller reads the current state
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // a failed run has recorded its own end; the caller reads it
        }
    }
}

/// <summary>Readiness checks of the job host (FR-STERP-JOBS-013): output folders writable, job queue depth.</summary>
public sealed class OutputFoldersCheck(OutputPaths paths) : Health.IReadinessCheck
{
    public string Name => "output-folders";

    public Task<(string Status, string? Description, JsonObject? Data)> CheckAsync(CancellationToken ct)
    {
        var data = new JsonObject();
        foreach (var sub in new[] { "spool", "logs", "work", "bacs", "mtd", "einvoice", "vat", "extracts" })
        {
            var dir = paths.Folder(sub);
            var probe = Path.Combine(dir, ".ready-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            data[sub] = dir;
        }
        return Task.FromResult<(string, string?, JsonObject?)>(("Healthy", $"output folders writable under {paths.Base}", data));
    }
}

public sealed class JobQueueCheck(JobService jobs) : Health.IReadinessCheck
{
    public string Name => "job-queue";

    public Task<(string Status, string? Description, JsonObject? Data)> CheckAsync(CancellationToken ct) =>
        Task.FromResult<(string, string?, JsonObject?)>(("Healthy", $"{jobs.ActiveRuns} run(s) executing",
            new JsonObject { ["depth"] = jobs.ActiveRuns }));
}
