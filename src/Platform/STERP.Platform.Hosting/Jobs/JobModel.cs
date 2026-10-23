using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Jobs;

namespace Sterp.Platform.Hosting.Jobs;

public sealed class SterpJobOptions
{
    public const string Section = "Sterp:Jobs";

    /// <summary>
    /// Target output root (contracts 8.5; RUNPATH STERP_RUN equivalent). A relative value is resolved against the host's
    /// content root (src/Hosts/&lt;host&gt; from source, the deployable folder when published), so the default is the
    /// repository's run folder.
    /// </summary>
    public string OutputRoot { get; set; } = "../../../run";

    /// <summary>Legacy run root, read only (contracts 8.5); default: the run folder of the legacy STERP-App clone next to this repository.</summary>
    public string LegacyRunRoot { get; set; } = "../../../../STERP-App/run";

    /// <summary>Maximum concurrently executing runs; 0 = no limit (FR-STERP-PLATFORM-057 step 7).</summary>
    public int MaxConcurrentRuns { get; set; }

    /// <summary>
    /// jobs_getRun of a run still executing in this host waits up to this long for it to end before answering, so a poller
    /// reads the final state as soon as it exists (the legacy programs ran synchronously); 0 answers at once.
    /// </summary>
    public int RunPollWaitMilliseconds { get; set; } = 1500;
}

/// <summary>How a run was submitted (FR-STERP-PLATFORM-057).</summary>
public enum JobEntryPoint
{
    /// <summary>A slice's POST /api/{segment}/v1/jobs/{jobCode}/runs.</summary>
    Slice,
    /// <summary>platform_launchCatalogueReport (ReportService.Run).</summary>
    Catalogue,
    /// <summary>platform_submitConsoleJob (STERPOPS).</summary>
    Console,
    /// <summary>jobs_restartRun.</summary>
    Restart,
    /// <summary>A step of a job stream.</summary>
    StreamStep,
}

public static class JobEntryPoints
{
    /// <summary>PlatformJobExecution.entryPoint: SLICE, CATALOGUE, CONSOLE, STREAM-STEP or RESTART.</summary>
    public static string ManifestName(this JobEntryPoint e) => e switch
    {
        JobEntryPoint.Catalogue => "CATALOGUE",
        JobEntryPoint.Console => "CONSOLE",
        JobEntryPoint.Restart => "RESTART",
        JobEntryPoint.StreamStep => "STREAM-STEP",
        _ => "SLICE",
    };

    public static JobEntryPoint Parse(string? name) => name switch
    {
        "CATALOGUE" => JobEntryPoint.Catalogue,
        "CONSOLE" => JobEntryPoint.Console,
        "RESTART" => JobEntryPoint.Restart,
        "STREAM-STEP" => JobEntryPoint.StreamStep,
        _ => JobEntryPoint.Slice,
    };
}

/// <summary>core.usp_JobRunStart values (JOBLOG START / ReportService.JobStart / SETENV).</summary>
public sealed record JobRegistration(string JobName, string? CompanyCode, string? SubmittedBy, string? SiteCode, string? Arguments);

/// <summary>core.usp_JobRunEnd values; Status null = the legacy program never ended its registration (SME-009).</summary>
public sealed record JobEnd(string? Status, int ReturnCode, int? RowsProcessed = null, string? LogFile = null, string? SpoolFile = null, string? Message = null);

/// <summary>What a pre-registration check sees (the checks the legacy program made before its JOBLOG START).</summary>
public sealed class JobPreCheckContext(JobDefinition definition, string commandLine, SterpContext session, IServiceProvider services, JsonObject parameters)
{
    public JobDefinition Definition { get; } = definition;

    /// <summary>The command line as the legacy executable received it (argv rebuilt, quotes consumed).</summary>
    public string CommandLine { get; } = CommandLineArgs.AsSeenByProgram(commandLine);
    public CommandLineArgs Args { get; } = CommandLineArgs.Parse(CommandLineArgs.AsSeenByProgram(commandLine));
    public SterpContext Session { get; } = session;
    public IServiceProvider Services { get; } = services;

    /// <summary>The typed parameter object of a slice submission (empty for other entry points).</summary>
    public JsonObject Parameters { get; } = parameters;
}

/// <summary>
/// One re-implemented batch program or job stream (contracts 8.1). Program handlers reproduce their legacy unit of work,
/// console messages, spool listing and return code (0 clean, 4 warning, &gt;= 8 failure). A program sets
/// <see cref="Execute"/>; a stream sets <see cref="Stream"/> (the slice-owned jobs\&lt;STREAM&gt;.cmd script run by the platform
/// stream engine, FR-STERP-PLATFORM-049).
/// </summary>
public sealed class JobDefinition
{
    private readonly bool _isStream;

    /// <summary>Job code of the submission path (program name, stream name or catalogue code such as JOB.DDCOLL).</summary>
    public required string JobCode { get; init; }

    public required string Slice { get; init; }

    /// <summary>Legacy program (or stream) name; defaults to the stream name or the job code.</summary>
    public string? ProgramName { get; init; }

    public string Program => ProgramName ?? Stream?.Name ?? JobCode;

    /// <summary>True for a job stream (a definition with a <see cref="Stream"/> script).</summary>
    public bool IsStream
    {
        get => _isStream || Stream is not null;
        init => _isStream = value;
    }

    /// <summary>The stream script (steps, @SQL steps, inline logic) executed by the platform stream engine.</summary>
    public StreamScript? Stream { get; init; }

    /// <summary>The slice's submission operation (POST .../jobs/{jobCode}/runs), when it has one.</summary>
    public string? SubmitOperationId { get; init; }

    /// <summary>Parameter keys in schema order (ReportService.BuildArguments order for slice submissions).</summary>
    public IReadOnlyList<string> ParameterOrder { get; init; } = [];

    /// <summary>When true, USER is added (caller) to a slice submission's command line when absent (contracts 8.2).</summary>
    public bool DefaultUserToCaller { get; init; } = true;

    /// <summary>
    /// Checks the legacy program made before registering; a non-null failed result refuses with nothing registered (422 at a
    /// submission; inside a stream the program ends with that return code and <see cref="RefusalDisplay"/> on its console).
    /// </summary>
    public Func<JobPreCheckContext, Task<LegacyResult?>>? PreCheck { get; init; }

    /// <summary>Console lines the legacy program displayed for a <see cref="PreCheck"/> refusal when it ran as a stream step (default: the message).</summary>
    public Func<LegacyResult, IEnumerable<string>>? RefusalDisplay { get; init; }

    /// <summary>The program's own JOBLOG START values; null = one row named after the job code (contracts 8.3).</summary>
    public Func<JobPreCheckContext, JobRegistration?>? SelfRegistration { get; init; }

    /// <summary>Additional (step, program, slice) dependencies of a stream beyond the RUNSTEP calls its script declares.</summary>
    public IReadOnlyList<(string Step, string Program, string Slice)> StepPrograms { get; init; } = [];

    /// <summary>Executes the program; returns the process exit code. Required for programs, unused for streams.</summary>
    public Func<JobRunContext, Task<int>>? Execute { get; init; }

    /// <summary>
    /// END values of the program's own registration from its exit code (default: JOBLOG END as the FIN/XMD suites map it -
    /// 0 COMPLETE->OK, 4 WARNING, other FAILED, with rows, log and spool).
    /// </summary>
    public Func<JobRunContext, int, JobEnd>? EndValues { get; init; }
}

/// <summary>Registry of job definitions contributed by the slices (FR-STERP-JOBS-002).</summary>
public sealed class JobCatalog
{
    private readonly Dictionary<string, JobDefinition> _byCode = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<JobDefinition> All => _byCode.Values;

    public void Add(JobDefinition definition)
    {
        if (definition.IsStream && definition.Stream is null)
        {
            throw new InvalidOperationException($"Stream {definition.JobCode} must define its Stream script (StreamScript.Create(...).Build()).");
        }
        if (!definition.IsStream && definition.Execute is null)
        {
            throw new InvalidOperationException($"Program {definition.JobCode} must define Execute.");
        }
        if (!_byCode.TryAdd(definition.JobCode, definition))
        {
            throw new InvalidOperationException($"Job {definition.JobCode} is already registered.");
        }
    }

    public JobDefinition? Find(string jobCode) => _byCode.GetValueOrDefault(jobCode.Trim());

    public JobDefinition? FindProgram(string programName) =>
        _byCode.Values.FirstOrDefault(d => !d.IsStream && string.Equals(d.Program, programName.Trim(), StringComparison.OrdinalIgnoreCase));

    public JobDefinition? FindStream(string streamName) =>
        _byCode.Values.FirstOrDefault(d => d.IsStream && string.Equals(d.Program, streamName.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>RUNPATH (FR-STERP-PLATFORM-026): output folders and file names under the configured run root.</summary>
public sealed class OutputPaths(SterpJobOptions options)
{
    private static readonly Dictionary<string, (string Sub, string Ext)> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPOOL"] = ("spool", "lst"), ["BACS"] = ("bacs", "txt"), ["MTD"] = ("mtd", "json"), ["EINVOICE"] = ("einvoice", "xml"),
        ["EXTRACTS"] = ("extracts", "csv"), ["LOGS"] = ("logs", "log"), ["WORK"] = ("work", "tmp"),
    };

    /// <summary>BASE: the run directory with one trailing separator removed.</summary>
    public string Base => options.OutputRoot.EndsWith('\\') || options.OutputRoot.EndsWith('/') ? options.OutputRoot[..^1] : options.OutputRoot;

    public string LegacyBase => options.LegacyRunRoot.TrimEnd('\\', '/');

    /// <summary>STERP_HOME: the folder that holds the run root (SETENV.cmd: STERP_RUN = STERP_HOME\run).</summary>
    public string Home => Path.GetDirectoryName(Path.GetFullPath(Base)) ?? Base;

    public string Folder(string sub)
    {
        var dir = Path.Combine(Base, sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// BUILD (RUNPATH.cob 2000-BUILD .. 2300-FILE-NAME): '&lt;base&gt;\&lt;sub&gt;\&lt;id&gt;[-&lt;company&gt;][-&lt;stamp&gt;].&lt;ext&gt;'.
    /// The folder is upper-cased; the stamp is used only when stamping is on, and a caller stamp is inserted verbatim as its
    /// 14-byte field holds it (untrimmed, BRF-RUNPATH-01); otherwise the local clock yyyyMMddHHmmss.
    /// </summary>
    public LegacyResult Build(string folder, string reportId, string? company, string? extension, string? callerStamp, bool stamp, out string path)
    {
        path = "";
        var f = (folder ?? "").Trim().ToUpperInvariant();
        if (!Folders.TryGetValue(f, out var sub))
        {
            return LegacyResult.Error("RUN0001", $"Unknown output folder {f} - use SPOOL BACS MTD EINVOICE EXTRACTS LOGS or WORK");
        }
        if (string.IsNullOrWhiteSpace(reportId)) return LegacyResult.Error("RUN0002", "A report or file identifier is required");
        var ext = string.IsNullOrWhiteSpace(extension) ? sub.Ext : extension.Trim();
        var parts = new StringBuilder(reportId.Trim());
        if (!string.IsNullOrWhiteSpace(company)) parts.Append('-').Append(company.Trim());
        if (stamp)
        {
            var s = !string.IsNullOrWhiteSpace(callerStamp)
                ? CobolText.Fit(callerStamp, 14)
                : DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            parts.Append('-').Append(s);
        }
        // the target writes the file, so the folder must exist (legacy SETENV/the run layout created it beforehand)
        path = Path.Combine(Folder(sub.Sub), parts + "." + ext);
        return LegacyResult.Ok($"Output file {path}");
    }

    /// <summary>True when the path lies under the target or the legacy run root (contracts 8.5; else 403 API0021).</summary>
    public bool IsUnderRoots(string path, out bool legacy)
    {
        legacy = false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        if (Under(full, Base)) return true;
        if (Under(full, LegacyBase)) { legacy = true; return true; }
        return false;
    }

    private static bool Under(string full, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A run's console (stdout/stderr interleaved) - the program's DISPLAY output, written to its SYSOUT in Windows-1252.</summary>
public sealed class JobConsole : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly StringBuilder _text = new();
    private readonly object _gate = new();

    public JobConsole(string path, bool append = false)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite), CobolText.Ansi) { AutoFlush = true, NewLine = "\r\n" };
    }

    /// <summary>A console captured in memory only (the catalogue launcher writes its log when the run ends, ReportService.vb:171-178).</summary>
    public JobConsole()
    {
    }

    /// <summary>The file the console is written to; '' for a memory console.</summary>
    public string Path { get; } = "";

    public string Text
    {
        get { lock (_gate) return _text.ToString(); }
    }

    public void WriteLine(string line = "")
    {
        lock (_gate)
        {
            _text.Append(line).Append("\r\n");
            _writer?.WriteLine(line);
        }
    }

    public void Dispose() => _writer?.Dispose();
}

/// <summary>
/// A listing file in Windows-1252 (contracts 8.5). Opened like GnuCOBOL OPEN OUTPUT - no share lock - so two runs that build
/// the same time-stamped name in one second both open it, as the legacy runs did. Legacy Report Writer listings are laid out
/// by <see cref="Sterp.Platform.Kernel.Reporting.GnuReportWriter"/> (no form feeds; pages are padded with blank lines).
/// </summary>
public sealed class SpoolWriter : IDisposable
{
    private readonly StreamWriter _writer;

    public SpoolWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), CobolText.Ansi) { NewLine = "\r\n" };
    }

    public string Path { get; }

    public int Lines { get; private set; }

    public void WriteLine(string line = "")
    {
        _writer.WriteLine(line.TrimEnd());
        Lines++;
    }

    /// <summary>
    /// Writes a form feed (X'0C'). The GnuCOBOL 3.2 Report Writer listings of the legacy programs contain none (pages are
    /// padded with blank lines, see GnuReportWriter); use this only where a program's evidenced output has one.
    /// </summary>
    public void PageBreak() => _writer.Write('\f');

    public void Dispose() => _writer.Dispose();
}

/// <summary>Everything a program handler needs while it runs (the legacy batch process environment).</summary>
public sealed class JobRunContext
{
    public JobRunContext(long jobRunId, JobDefinition definition, string commandLine, SterpContext session, IServiceProvider services,
        JobConsole console, OutputPaths paths, JobManifest manifest, CancellationToken cancellation)
    {
        JobRunId = jobRunId;
        Definition = definition;
        SubmittedCommandLine = commandLine;
        CommandLine = CommandLineArgs.AsSeenByProgram(commandLine);
        Args = CommandLineArgs.Parse(CommandLine);
        Session = session;
        Services = services;
        Console = console;
        Paths = paths;
        Manifest = manifest;
        Cancellation = cancellation;
    }

    /// <summary>The run id returned to the submitter (the program's own registration, or the launcher row).</summary>
    public long JobRunId { get; }

    /// <summary>The program's own core.JobRun row, when it registered itself.</summary>
    public long? ProgramRunId { get; set; }

    public JobDefinition Definition { get; }

    /// <summary>
    /// The command line as the legacy executable received it: argv rebuilt by the C runtime and joined by GnuCOBOL (quotes
    /// consumed). The program's own JOBLOG registration and CMDARGS see this text.
    /// </summary>
    public string CommandLine { get; }

    /// <summary>The argument string the launcher built (ReportService.BuildArguments, STERPOPS, stream step PARM).</summary>
    public string SubmittedCommandLine { get; }

    public CommandLineArgs Args { get; }

    public SterpContext Session { get; }

    public IServiceProvider Services { get; }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public JobConsole Console { get; }

    public OutputPaths Paths { get; }

    public JobManifest Manifest { get; }

    public CancellationToken Cancellation { get; }

    /// <summary>Announced spool (the last STERP-I-SPOOL path); recorded as core.JobRun.SpoolFile.</summary>
    public string? SpoolFile { get; set; }

    public List<string> SpoolFiles { get; } = [];

    public int? RowsProcessed { get; set; }

    public string? Message { get; set; }

    /// <summary>Report data returned by GET /api/jobs/v1/runs/{id}/result (contracts 8.5).</summary>
    public JsonNode? Result { get; set; }

    /// <summary>Explicit END values (overrides the definition's mapping), e.g. a program whose END is refused (SME-009).</summary>
    public JobEnd? EndOverride { get; set; }

    /// <summary>Writes a console line (DISPLAY).</summary>
    public void Display(string line) => Console.WriteLine(line);

    /// <summary>Opens a spool file via RUNPATH BUILD SPOOL (report id, company, timestamp).</summary>
    public SpoolWriter OpenSpool(string reportId, string? company, string? extension = null, string folder = "SPOOL")
    {
        var r = Paths.Build(folder, reportId, company, extension, null, true, out var path);
        if (!r.IsOk) throw new LegacyOutcomeException(r);
        var w = new SpoolWriter(path);
        SpoolFiles.Add(path);
        return w;
    }

    /// <summary>The final 'STERP-I-SPOOL &lt;path&gt;' console line (FR-STERP-PLATFORM-028).</summary>
    public void AnnounceSpool(string path)
    {
        Display("STERP-I-SPOOL " + path);
        SpoolFile = path;
        if (!SpoolFiles.Contains(path)) SpoolFiles.Add(path);
    }
}

/// <summary>Run manifest (FR-STERP-PLATFORM-047): &lt;root&gt;\work\jobs\&lt;jobRunId&gt;.json, the execution block of jobs_getRun.</summary>
public sealed class JobManifest
{
    public string EntryPoint { get; set; } = "SLICE";

    /// <summary>Entry point whose rules the run followed (a restart re-submits through the original entry point).</summary>
    public string SubmitRules { get; set; } = "SLICE";

    public bool IsStream { get; set; }

    /// <summary>The catalogue launcher's own core.JobRun row.</summary>
    public long? LauncherJobRunId { get; set; }

    /// <summary>The program's own (or the D-PLAT-09) row, or the stream's SETENV row.</summary>
    public long? OwnJobRunId { get; set; }

    /// <summary>For a STREAM-STEP manifest: the stream run the step belongs to.</summary>
    public long? StreamJobRunId { get; set; }

    /// <summary>Operator console page-open date (OPS log stamp of a restart).</summary>
    public DateOnly? SessionStartDate { get; set; }

    /// <summary>Run console file (launcher log, OPS log, program console or stream console).</summary>
    public string? ConsoleFile { get; set; }

    /// <summary>core.JobRun.StartedAt of every row this manifest serves (id -&gt; StartedAt); detects a reused id after a database restore.</summary>
    public Dictionary<string, DateTime> Rows { get; set; } = [];

    public string JobCode { get; set; } = "";
    public string? ReportCode { get; set; }
    public string ProgramName { get; set; } = "";
    public string Slice { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public string State { get; set; } = "ACCEPTED";
    public int? ExitCode { get; set; }
    public DateTime AcceptedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public List<long> RelatedJobRunIds { get; set; } = [];
    public long? RestartOfJobRunId { get; set; }
    public List<JobStepRecord> Steps { get; set; } = [];
    public List<string> SpoolFiles { get; set; } = [];
    public bool HasResult { get; set; }
    public string? LogFile { get; set; }
    public string? Error { get; set; }
}

public sealed class JobStepRecord
{
    public string StepName { get; set; } = "";
    public string Program { get; set; } = "";
    public string Parm { get; set; } = "";
    public int? CondCode { get; set; }
    public int MaxCondCode { get; set; }
}

public sealed class JobManifestStore(OutputPaths paths)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ConcurrentDictionary<long, object> _locks = new();

    private string Dir => paths.Folder(Path.Combine("work", "jobs"));

    public string ManifestPath(long id) => Path.Combine(Dir, id.ToString(CultureInfo.InvariantCulture) + ".json");

    public string ResultPath(long id) => Path.Combine(Dir, id.ToString(CultureInfo.InvariantCulture) + ".result.json");

    public void Save(long id, JobManifest m)
    {
        lock (_locks.GetOrAdd(id, _ => new object()))
        {
            File.WriteAllText(ManifestPath(id), JsonSerializer.Serialize(m, Json), Encoding.UTF8);
        }
    }

    /// <summary>Saves one execution's manifest under every run id that serves it (catalogue launcher and stream row).</summary>
    public void Save(IEnumerable<long> ids, JobManifest m)
    {
        foreach (var id in ids.Distinct()) Save(id, m);
    }

    public JobManifest? Load(long id)
    {
        var p = ManifestPath(id);
        if (!File.Exists(p)) return null;
        lock (_locks.GetOrAdd(id, _ => new object()))
        {
            return JsonSerializer.Deserialize<JobManifest>(File.ReadAllText(p, Encoding.UTF8), Json);
        }
    }

    public void SaveResult(long id, JsonNode result) => File.WriteAllText(ResultPath(id), result.ToJsonString(), Encoding.UTF8);

    public JsonNode? LoadResult(long id)
    {
        var p = ResultPath(id);
        return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) : null;
    }

    /// <summary>Job-host restart: runs still ACCEPTED/RUNNING become LOST; core.JobRun stays as it is (FR-STERP-PLATFORM-057).</summary>
    public int MarkLost()
    {
        var n = 0;
        foreach (var file in Directory.GetFiles(Dir, "*.json").Where(f => !f.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase)))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            var m = Load(id);
            if (m is null || m.State is not ("ACCEPTED" or "RUNNING")) continue;
            m.State = "LOST";
            Save(id, m);
            n++;
        }
        return n;
    }
}
