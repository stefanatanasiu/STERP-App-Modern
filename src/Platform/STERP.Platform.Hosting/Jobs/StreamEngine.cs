using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Hosting.Jobs;

/// <summary>Where a stream block continues: the next block, or a label (goto; ABEND and EOJ are the built-in end blocks).</summary>
public readonly record struct StreamFlow(string? Label)
{
    public static StreamFlow Next => new(null);

    public static StreamFlow Abend => new(StreamScript.AbendLabel);

    public static StreamFlow Eoj => new(StreamScript.EojLabel);

    public static StreamFlow Goto(string label) => new(label);

    /// <summary>The gating line after every legacy step: <c>if %LASTCC% GEQ 8 goto ABEND</c>.</summary>
    public static StreamFlow GateAbend(StreamContext s) => s.LastCC >= 8 ? Abend : Next;
}

/// <summary>A RUNSTEP call declared by a stream script: step name and program (or @SQL).</summary>
public sealed record StreamStepInfo(string StepName, string Program);

internal sealed record StreamBlock(string Label, Func<StreamContext, Task<StreamFlow>> Body, IReadOnlyList<StreamStepInfo> Steps);

/// <summary>
/// A job stream script (jobs\&lt;STREAM&gt;.cmd) owned by a business slice and executed by the platform stream engine
/// (FR-STERP-PLATFORM-049; STERP-PLATFORM 4.10). The engine runs the SETENV prologue (parameters, defaults, SYSOUT job card,
/// registration), the optional <see cref="StreamScriptBuilder.Setup(Action{StreamContext})"/> code that legacy scripts placed
/// before <c>if defined P_RESTART goto %P_RESTART%</c>, then the labelled blocks in order with goto semantics, and the
/// ABEND/EOJ end blocks with the JOBEND epilogue. Build one with <see cref="Create"/>.
/// </summary>
public sealed partial class StreamScript
{
    public const string AbendLabel = "ABEND";
    public const string EojLabel = "EOJ";
    public const string SqlProgram = "@SQL";

    /// <summary>The standard ABEND line of every stream (NIGHTLY.cmd:56).</summary>
    public static string TerminatedLine(StreamContext s) => "//*  STREAM TERMINATED BY COND CODE " + s.LastCC.ToString(CultureInfo.InvariantCulture);

    internal StreamScript(string name, IReadOnlyList<StreamBlock> blocks, Func<StreamContext, Task>? setup, Func<StreamContext, IEnumerable<string>> abendLines)
    {
        Name = name;
        Blocks = blocks;
        Setup = setup;
        AbendLines = abendLines;
    }

    /// <summary>The JOBNAME the script passes to SETENV (e.g. MONTHEND) - SYSOUT name, job card and core.JobRun.JobName.</summary>
    public string Name { get; }

    internal IReadOnlyList<StreamBlock> Blocks { get; }

    internal Func<StreamContext, Task>? Setup { get; }

    internal Func<StreamContext, IEnumerable<string>> AbendLines { get; }

    /// <summary>Labels of the script in order (without ABEND/EOJ).</summary>
    public IReadOnlyList<string> Labels => Blocks.Select(b => b.Label).ToList();

    /// <summary>Every declared RUNSTEP call (step name, program or @SQL).</summary>
    public IReadOnlyList<StreamStepInfo> Steps => Blocks.SelectMany(b => b.Steps).ToList();

    /// <summary>Programs the stream runs (the @SQL steps excluded) - checked for delivery before anything is registered.</summary>
    public IReadOnlyList<StreamStepInfo> ProgramSteps => Steps.Where(s => !string.Equals(s.Program, SqlProgram, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>jobs_restartRun labels: a STEPnnn label of the script, ABEND or EOJ (STERP-PLATFORM 4.11, API0024).</summary>
    public bool IsRestartLabel(string label)
    {
        var l = label.Trim();
        if (string.Equals(l, AbendLabel, StringComparison.OrdinalIgnoreCase) || string.Equals(l, EojLabel, StringComparison.OrdinalIgnoreCase)) return true;
        return StepLabel().IsMatch(l) && Blocks.Any(b => string.Equals(b.Label, l, StringComparison.OrdinalIgnoreCase));
    }

    public static StreamScriptBuilder Create(string streamName) => new(streamName);

    [GeneratedRegex("^STEP[0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex StepLabel();
}

/// <summary>Builds a <see cref="StreamScript"/> block by block in the order of the legacy .cmd file.</summary>
public sealed class StreamScriptBuilder
{
    private readonly string _name;
    private readonly List<StreamBlock> _blocks = [];
    private Func<StreamContext, Task>? _setup;
    private Func<StreamContext, IEnumerable<string>> _abend = s => [StreamScript.TerminatedLine(s)];

    internal StreamScriptBuilder(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A stream name is required.", nameof(name));
        _name = name.Trim().ToUpperInvariant();
    }

    /// <summary>Statements between <c>call SETENV.cmd</c> and <c>if defined P_RESTART goto %P_RESTART%</c> (XMDCONV.cmd:16-17).</summary>
    public StreamScriptBuilder Setup(Action<StreamContext> action)
    {
        _setup = s =>
        {
            action(s);
            return Task.CompletedTask;
        };
        return this;
    }

    public StreamScriptBuilder SetupAsync(Func<StreamContext, Task> action)
    {
        _setup = action;
        return this;
    }

    /// <summary>
    /// The standard block: <c>:LABEL</c>, <c>call RUNSTEP.cmd LABEL PROGRAM "PARM"</c>, <c>if %LASTCC% GEQ 8 goto ABEND</c>.
    /// The PARM is built when the step runs, from the stream variables (e.g. <c>s =&gt; $"CO={s.Var("CO")} USER={s.Var("USER")}"</c>).
    /// </summary>
    public StreamScriptBuilder Step(string label, string program, Func<StreamContext, string> parm) =>
        Block(label, async s =>
        {
            await s.RunStepAsync(label, program, parm(s));
            return StreamFlow.GateAbend(s);
        }, new StreamStepInfo(label, program.Trim().ToUpperInvariant()));

    /// <summary>
    /// The standard @SQL block: RUNSTEP with PGM=@SQL. <paramref name="displayParm"/> is the legacy PARM text written to SYSOUT
    /// (the statement with the values the legacy script concatenated); <paramref name="statement"/> is the same statement with
    /// those values as parameters (D-PLAT-08), executed with SET NOCOUNT ON on its own connection as sqlcmd did.
    /// </summary>
    public StreamScriptBuilder SqlStep(string label, Func<StreamContext, string> displayParm, string statement, Func<StreamContext, object?>? parameters = null) =>
        Block(label, async s =>
        {
            await s.RunSqlStepAsync(label, displayParm(s), statement, parameters?.Invoke(s));
            return StreamFlow.GateAbend(s);
        }, new StreamStepInfo(label, StreamScript.SqlProgram));

    /// <summary>
    /// A custom block (inline lookups, conditional PARM text, custom SYSOUT lines, jumps). Declare every RUNSTEP call the block
    /// may make in <paramref name="steps"/> so the delivery check (501) covers its programs.
    /// </summary>
    public StreamScriptBuilder Block(string label, Func<StreamContext, Task<StreamFlow>> body, params StreamStepInfo[] steps)
    {
        var l = label.Trim();
        if (l.Length == 0) throw new ArgumentException("A label is required.", nameof(label));
        if (string.Equals(l, StreamScript.AbendLabel, StringComparison.OrdinalIgnoreCase) || string.Equals(l, StreamScript.EojLabel, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("ABEND and EOJ are the engine's end blocks; customise ABEND with Abend(...).", nameof(label));
        }
        if (_blocks.Any(b => string.Equals(b.Label, l, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"Label {l} is defined twice.", nameof(label));
        _blocks.Add(new StreamBlock(l, body, steps));
        return this;
    }

    /// <summary>The SYSOUT lines the script writes at :ABEND before calling JOBEND (default: STREAM TERMINATED BY COND CODE).</summary>
    public StreamScriptBuilder Abend(Func<StreamContext, IEnumerable<string>> lines)
    {
        _abend = lines;
        return this;
    }

    public StreamScript Build() => new(_name, _blocks.ToList(), _setup, _abend);
}

/// <summary>What a stream block sees: the SETENV variables, LASTCC/MAXCC, SYSOUT and console, and the RUNSTEP primitives.</summary>
public sealed class StreamContext
{
    private readonly StreamEngine _engine;

    internal StreamContext(StreamEngine engine, StreamPrologue prologue, JobConsole sysout, JobConsole console, IServiceProvider services, CancellationToken ct)
    {
        _engine = engine;
        Prologue = prologue;
        SysoutConsole = sysout;
        RunConsole = console;
        Services = services;
        Cancellation = ct;
    }

    internal StreamPrologue Prologue { get; }

    internal JobConsole SysoutConsole { get; }

    internal JobConsole RunConsole { get; }

    public string JobName => Prologue.JobName;

    /// <summary>The stream's own core.JobRun id (JOBRUNID), when the prologue registered it.</summary>
    public long? JobRunId { get; internal set; }

    public string SysoutPath => SysoutConsole.Path;

    public IServiceProvider Services { get; }

    public CancellationToken Cancellation { get; }

    /// <summary>Return code of the last step (LASTCC).</summary>
    public int LastCC { get; set; }

    /// <summary>Highest step return code so far (MAXCC); the stream's return code.</summary>
    public int MaxCC { get; set; }

    /// <summary>%P_&lt;name&gt;%: the value, or '' when undefined (cmd expands an undefined variable to nothing in a batch file).</summary>
    public string Var(string name) => Prologue.Get(name) ?? "";

    /// <summary><c>if defined P_&lt;name&gt;</c>.</summary>
    public bool IsDefined(string name) => Prologue.Get(name) is not null;

    /// <summary><c>set "P_&lt;name&gt;=value"</c> (an empty value undefines the variable).</summary>
    public void Set(string name, string? value) => Prologue.Set(name, value);

    /// <summary><c>echo line&gt;&gt; "%SYSOUT%"</c>.</summary>
    public void Sysout(string line) => SysoutConsole.WriteLine(line);

    /// <summary><c>echo line</c> to the stream's console (the launcher log, operator console or caller).</summary>
    public void Console(string line) => RunConsole.WriteLine(line);

    /// <summary>RUNSTEP.cmd for a program step; sets <see cref="LastCC"/> and <see cref="MaxCC"/>.</summary>
    public Task RunStepAsync(string step, string program, string parm) => _engine.RunStepAsync(this, step, program, parm);

    /// <summary>RUNSTEP.cmd for an @SQL step; LASTCC 0 on success, 8 on any failure.</summary>
    public Task RunSqlStepAsync(string step, string displayParm, string statement, object? parameters = null) =>
        _engine.RunSqlStepAsync(this, step, displayParm, statement, parameters);

    /// <summary>
    /// An inline lookup <c>for /f "usebackq tokens=*" %%I in (`sqlcmd -h -1 -W -Q "SET NOCOUNT ON; ..."`) do set ...</c>: the last
    /// non-empty output line (columns separated by one space) with leading blanks removed, or null when there is none; a
    /// failure yields the last line of the error text, as the legacy loop captured it.
    /// </summary>
    public Task<string?> QueryLastLineAsync(string statement, object? parameters = null) => _engine.QueryLastLineAsync(statement, parameters, Cancellation);
}

/// <summary>
/// SETENV.cmd (SETENV.cmd:11-101): parameters parsed as cmd.exe delivers them to a batch file - a quoted 'KEY=VALUE' token, or
/// an unquoted KEY followed by VALUE as the next token, later wins - over the defaults CO=STC, USER=OPS, MODE=PREVIEW,
/// DAYS=0; the stamp from the clock; ASAT, PERIOD and YEAR defaults with the April fiscal start; PP and YYYYPP.
/// </summary>
public sealed class StreamPrologue
{
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);

    private StreamPrologue(string jobName, string commandLine, DateTime clock)
    {
        JobName = jobName;
        CommandLine = commandLine;
        Clock = clock;
        Stamp = clock.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    }

    public string JobName { get; }

    public string CommandLine { get; }

    public DateTime Clock { get; }

    /// <summary>JOBSTAMP yyyyMMddHHmmss.</summary>
    public string Stamp { get; }

    public IReadOnlyDictionary<string, string> Variables => _vars;

    public string? Get(string name) => _vars.TryGetValue(name, out var v) ? v : null;

    public void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) _vars.Remove(name);
        else _vars[name] = value;
    }

    private string V(string name) => Get(name) ?? "";

    /// <summary>core.usp_JobRunStart @Arguments of the stream row (SETENV.cmd:99).</summary>
    public string Arguments => $"MODE={V("MODE")} ASAT={V("ASAT")} YEAR={V("YEAR")} PERIOD={V("PERIOD")}";

    public string Company => V("CO");

    public string User => V("USER");

    /// <summary>P_RESTART when defined.</summary>
    public string? Restart => Get("RESTART");

    public static StreamPrologue Parse(string jobName, string commandLine, DateTime clock)
    {
        var p = new StreamPrologue(jobName.Trim().ToUpperInvariant(), commandLine, clock);
        p.Set("CO", "STC");
        p.Set("USER", "OPS");
        p.Set("MODE", "PREVIEW");
        p.Set("DAYS", "0");
        var tokens = BatchArguments.Split(commandLine);
        var i = 0;
        while (i < tokens.Count)
        {
            var ptok = BatchArguments.Unquote(tokens[i]);
            if (ptok.Length == 0) break;
            var pval = i + 1 < tokens.Count ? BatchArguments.Unquote(tokens[i + 1]) : "";
            var eqParts = ptok.Split('=', StringSplitOptions.RemoveEmptyEntries);
            if (!ptok.StartsWith(';') && eqParts.Length >= 2)
            {
                // for /f "tokens=1,* delims==" %%K in ("%PTOK%") do set "P_%%K=%%L"
                var key = eqParts[0];
                var at = ptok.IndexOf(key, StringComparison.Ordinal) + key.Length;
                while (at < ptok.Length && ptok[at] == '=') at++;
                p.CmdSet("P_" + key + "=" + ptok[at..]);
                i += 1;
            }
            else
            {
                // for /f %%K in ("%PTOK%") do set "P_%%K=%PVAL%" & shift
                var first = ptok.StartsWith(';') ? null : ptok.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first is not null) p.CmdSet("P_" + first + "=" + pval);
                i += 2;
            }
        }
        if (p.Get("ASAT") is null) p.Set("ASAT", p.Stamp[..8]);
        var month = clock.Month;
        var year = clock.Year;
        var period = month >= 4 ? month - 3 : month + 9;
        if (month < 4) year -= 1;
        if (p.Get("PERIOD") is null) p.Set("PERIOD", period.ToString(CultureInfo.InvariantCulture));
        if (p.Get("YEAR") is null) p.Set("YEAR", year.ToString(CultureInfo.InvariantCulture));
        var pp = "0" + p.V("PERIOD");
        p.Set("PP", pp[^2..]);
        p.Set("YYYYPP", p.V("YEAR") + p.V("PP"));
        return p;
    }

    /// <summary>cmd <c>set "NAME=value"</c>: the name ends at the first '='; an empty value deletes the variable.</summary>
    private void CmdSet(string assignment)
    {
        var eq = assignment.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0) return;
        var name = assignment[..eq];
        if (!name.StartsWith("P_", StringComparison.OrdinalIgnoreCase)) return;
        Set(name[2..], assignment[(eq + 1)..]);
    }
}

/// <summary>cmd.exe batch argument splitting (%1, %2 ... of a .cmd file) and %~n quote removal.</summary>
public static class BatchArguments
{
    /// <summary>Tokens split at space, tab, comma, semicolon and '=' outside double quotes; quotes are kept in the token.</summary>
    public static List<string> Split(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            if (!quoted && c is ' ' or '\t' or ',' or ';' or '=')
            {
                if (sb.Length > 0) tokens.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>%~n: the surrounding double quotes removed.</summary>
    public static string Unquote(string token)
    {
        var t = token;
        if (t.StartsWith('"')) t = t[1..];
        if (t.EndsWith('"')) t = t[..^1];
        return t;
    }
}

/// <summary>Renders statement output the way <c>sqlcmd -W</c> printed it into SYSOUT (RUNSTEP.cmd:42).</summary>
public static class SqlcmdText
{
    public static string Value(DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return "NULL";
        var type = r.GetDataTypeName(i).ToLowerInvariant();
        var v = r.GetValue(i);
        return v switch
        {
            string s => s.TrimEnd(' '),
            bool b => b ? "1" : "0",
            decimal d when type == "money" || type == "smallmoney" => d.ToString("0.0000", CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            DateTime dt when type == "date" => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt when type == "smalldatetime" => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTime dt when type == "datetime" => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateTime dt => Fraction(dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture), Scale(r, i)),
            DateTimeOffset dto => Fraction(dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture), Scale(r, i)) + dto.ToString(" zzz", CultureInfo.InvariantCulture),
            TimeSpan ts => Fraction(ts.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture), Scale(r, i)),
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            Guid g => g.ToString().ToUpperInvariant(),
            double db => db.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        };
    }

    private static int Scale(DbDataReader r, int i)
    {
        try
        {
            if (r.CanGetColumnSchema()) return r.GetColumnSchema()[i].NumericScale ?? 7;
        }
        catch (NotSupportedException)
        {
        }
        return 7;
    }

    private static string Fraction(string text, int scale)
    {
        var dot = text.LastIndexOf('.');
        if (dot < 0) return text;
        var keep = Math.Clamp(scale, 0, 7);
        return keep == 0 ? text[..dot] : text[..(dot + 1 + keep)];
    }

    /// <summary>sqlcmd error text: 'Msg n, Level l, State s, Server x[, Procedure p], Line n' then the message.</summary>
    public static IEnumerable<string> Error(SqlError e)
    {
        var head = new StringBuilder("Msg ").Append(e.Number.ToString(CultureInfo.InvariantCulture))
            .Append(", Level ").Append(e.Class.ToString(CultureInfo.InvariantCulture))
            .Append(", State ").Append(e.State.ToString(CultureInfo.InvariantCulture))
            .Append(", Server ").Append(e.Server);
        if (!string.IsNullOrEmpty(e.Procedure)) head.Append(", Procedure ").Append(e.Procedure);
        head.Append(", Line ").Append(e.LineNumber.ToString(CultureInfo.InvariantCulture));
        yield return head.ToString();
        foreach (var line in (e.Message ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) yield return line;
    }
}

/// <summary>
/// The stream engine (FR-STERP-PLATFORM-049; STERP-PLATFORM 4.10): SETENV prologue output and PEREND, the RESTART jump, the
/// blocks of the script with goto semantics, RUNSTEP (program and @SQL steps) and the ABEND/EOJ epilogue (JOBEND). One
/// instance runs one stream execution.
/// </summary>
internal sealed partial class StreamEngine
{
    private const int AbendIndex = -1;
    private const int EojIndex = -2;

    private readonly JobService _jobs;
    private readonly StreamScript _script;
    private readonly JobManifest _manifest;
    private readonly Action _saveManifest;
    private readonly SterpContextSnapshot _snapshot;
    private readonly ISqlConnectionFactory _connections;

    public StreamEngine(JobService jobs, JobDefinition definition, JobManifest manifest, Action saveManifest,
        SterpContextSnapshot snapshot, ISqlConnectionFactory connections)
    {
        _jobs = jobs;
        _script = definition.Stream ?? throw new InvalidOperationException($"Job {definition.JobCode} is not a stream.");
        _manifest = manifest;
        _saveManifest = saveManifest;
        _snapshot = snapshot;
        _connections = connections;
    }

    /// <summary>Runs the stream; returns its exit code (MAXCC, or 1 when a goto label does not exist).</summary>
    public async Task<int> RunAsync(StreamPrologue prologue, long? streamRunId, JobConsole console, IServiceProvider services, CancellationToken ct)
    {
        foreach (var d in new[] { "logs", "spool", "bacs", "mtd", "einvoice", "extracts", "work" }) _jobs.Paths.Folder(d);
        var sysoutPath = Path.Combine(_jobs.Paths.Folder("logs"), prologue.JobName + "-" + prologue.Stamp + ".log");
        using var sysout = new JobConsole(sysoutPath);
        var s = new StreamContext(this, prologue, sysout, console, services, ct) { JobRunId = streamRunId };
        await ResolvePeriodEndAsync(s);

        var job = prologue.JobName;
        s.Sysout($"//{job} JOB (STERP),'{job} SERVICE',CLASS=A,MSGCLASS=X");
        s.Sysout($"//*  SUBMITTED {CmdDate(prologue.Clock)} {CmdTime(prologue.Clock)} BY {s.Var("USER")} ON {Environment.MachineName}");
        s.Sysout($"//*  COMPANY={s.Var("CO")} SITE={s.Var("SITE")} MODE={s.Var("MODE")} ASAT={s.Var("ASAT")}");
        s.Sysout($"//*  FISCAL YEAR={s.Var("YEAR")} PERIOD={s.Var("PERIOD")} RUN={s.Var("RUN")}");
        s.Sysout($"//*  STERP_HOME={_jobs.Paths.Home}");
        s.Sysout($"//*  SYSOUT={sysoutPath}");
        s.Sysout("//JOBLIB  DD DSN=STERP.BIN.X86,DISP=SHR");
        s.Sysout("");
        s.Console($"STERP-I-0001 {job} started - SYSOUT {sysoutPath}");
        if (streamRunId is not null) s.Sysout($"//*  core.JobRun id {streamRunId.Value.ToString(CultureInfo.InvariantCulture)}");
        _manifest.LogFile = sysoutPath;
        _saveManifest();

        if (_script.Setup is not null) await _script.Setup(s);

        var index = 0;
        if (prologue.Restart is { } restart)
        {
            var target = Find(restart);
            if (target is null) return MissingLabel(s, restart);
            index = target.Value;
        }
        while (true)
        {
            if (index == AbendIndex) return await AbendAsync(s);
            if (index == EojIndex || index >= _script.Blocks.Count) return await EndAsync(s);
            var flow = await _script.Blocks[index].Body(s);
            if (flow.Label is null)
            {
                index++;
                continue;
            }
            var next = Find(flow.Label);
            if (next is null) return MissingLabel(s, flow.Label);
            index = next.Value;
        }
    }

    /// <summary>
    /// cmd.exe 'goto' to an undefined label: the message on the console and the batch file ends with exit code 1; JOBEND
    /// never runs, so a registered stream row stays RUNNING (U-PLAT-03, BC-PLATFORM-05 rejected).
    /// </summary>
    private static int MissingLabel(StreamContext s, string label)
    {
        s.Console("The system cannot find the batch label specified - " + label.Trim());
        return 1;
    }

    /// <summary>Index of a label: a script block, ABEND or EOJ; null when the script does not define it.</summary>
    private int? Find(string label)
    {
        var l = label.Trim();
        for (var i = 0; i < _script.Blocks.Count; i++)
        {
            if (string.Equals(_script.Blocks[i].Label, l, StringComparison.OrdinalIgnoreCase)) return i;
        }
        if (string.Equals(l, StreamScript.AbendLabel, StringComparison.OrdinalIgnoreCase)) return AbendIndex;
        if (string.Equals(l, StreamScript.EojLabel, StringComparison.OrdinalIgnoreCase)) return EojIndex;
        return null;
    }

    private async Task<int> AbendAsync(StreamContext s)
    {
        foreach (var line in _script.AbendLines(s)) s.Sysout(line);
        return await EndAsync(s);
    }

    /// <summary>JOBEND.cmd (JOBEND.cmd:13-27): status by MAXCC, end lines, console line, END when registered (errors ignored).</summary>
    private async Task<int> EndAsync(StreamContext s)
    {
        var cc = s.MaxCC;
        var status = JobService.JobendStatus(cc);
        var now = DateTime.Now;
        var ccText = cc.ToString(CultureInfo.InvariantCulture);
        s.Sysout("");
        s.Sysout($"//*  JOB {s.JobName} ENDED - MAXIMUM COND CODE {ccText} ({status})");
        s.Sysout($"//*  {CmdDate(now)} {CmdTime(now)}");
        s.Sysout("//");
        s.Console($"STERP-I-0009 {s.JobName} ended - MAXCC {ccText} ({status}) - {s.SysoutPath}");
        if (s.JobRunId is not null)
        {
            try
            {
                await JobService.EndAsync(s.Services.GetRequiredService<IProcedureRunner>(), s.JobRunId.Value,
                    new JobEnd(status, cc, null, s.SysoutPath, null, $"Job stream {s.JobName} ended with COND code {ccText}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _jobs.LogWarning("JobRunEnd: " + ex.Message);
            }
        }
        return cc;
    }

    /// <summary>
    /// SETENV P_PEREND (SETENV.cmd:79-81): EndDate (yyyymmdd) of fin.Period(CO, YEAR, PERIOD) when the answer is 8 digits,
    /// otherwise ASAT. The values are bound as parameters (D-PLAT-08).
    /// </summary>
    private async Task ResolvePeriodEndAsync(StreamContext s)
    {
        var line = await QueryLastLineAsync(
            "SELECT CONVERT(char(8), EndDate, 112) FROM fin.Period WHERE CompanyCode=@co AND FiscalYear=@year AND PeriodNo=@period;",
            new { co = s.Var("CO"), year = s.Var("YEAR"), period = s.Var("PERIOD") }, s.Cancellation);
        s.Set("PEREND", line is not null && PeriodEnd().IsMatch(line) ? line : s.Var("ASAT"));
    }

    // ------------------------------------------------------------------ RUNSTEP (RUNSTEP.cmd:15-50)

    private static void StepHeader(StreamContext s, string step, string program, string parm)
    {
        s.Sysout("");
        s.Sysout($"//{step}  EXEC PGM={program},PARM='{parm}'");
        s.Sysout("//SYSPRINT DD SYSOUT=*");
        s.Console($"STERP-I-0002 {step} {program} {parm}");
    }

    private void StepEnd(StreamContext s, string step, string program, string parm)
    {
        if (s.LastCC > s.MaxCC) s.MaxCC = s.LastCC;
        var last = s.LastCC.ToString(CultureInfo.InvariantCulture);
        s.Sysout($"//*  {step} ENDED - COND CODE {last}  (MAXCC {s.MaxCC.ToString(CultureInfo.InvariantCulture)})");
        if (s.LastCC >= 8) s.Console($"STERP-E-0008 {step} failed - COND CODE {last}");
        _manifest.Steps.Add(new JobStepRecord { StepName = step, Program = program, Parm = parm, CondCode = s.LastCC, MaxCondCode = s.MaxCC });
        _saveManifest();
    }

    public async Task RunStepAsync(StreamContext s, string step, string program, string parm)
    {
        if (string.Equals(program.Trim(), StreamScript.SqlProgram, StringComparison.OrdinalIgnoreCase))
        {
            await RunSqlStepAsync(s, step, parm, parm, null);
            return;
        }
        var pgm = program.Trim().ToUpperInvariant();
        StepHeader(s, step, pgm, parm);
        s.LastCC = 0;
        var definition = _jobs.Catalog.FindProgram(pgm);
        if (definition is null || !_jobs.IsDelivered(definition.Slice))
        {
            // RUNSTEP.cmd:29-34; cannot occur for an accepted stream (D-PLAT-07) unless a custom block runs an undeclared program
            s.Sysout($"//*  STEP BYPASSED - {pgm}.EXE IS NOT INSTALLED");
            s.Console($"STERP-W-0004 {step} bypassed - {pgm}.EXE not installed");
            s.LastCC = 4;
        }
        else
        {
            var (exit, ownRunId) = await _jobs.RunStepProgramAsync(definition, parm, s.SysoutConsole, _snapshot, s.JobRunId, _manifest, s.Cancellation);
            if (ownRunId is not null && !_manifest.RelatedJobRunIds.Contains(ownRunId.Value)) _manifest.RelatedJobRunIds.Add(ownRunId.Value);
            s.LastCC = exit;
        }
        StepEnd(s, step, pgm, parm);
    }

    public async Task RunSqlStepAsync(StreamContext s, string step, string displayParm, string statement, object? parameters)
    {
        StepHeader(s, step, StreamScript.SqlProgram, displayParm);
        var ok = await ExecuteSqlAsync("SET NOCOUNT ON; " + statement, parameters, s.Sysout, true, s.Cancellation);
        s.LastCC = ok ? 0 : 8;
        StepEnd(s, step, StreamScript.SqlProgram, displayParm);
    }

    /// <summary>
    /// sqlcmd -b -I -W -Q (RUNSTEP.cmd:42): every result set as header, dash line and rows with one blank between columns,
    /// messages (PRINT, RAISERROR &lt; 11) and error text written as sqlcmd printed them; false on any error.
    /// </summary>
    private async Task<bool> ExecuteSqlAsync(string statement, object? parameters, Action<string> write, bool headers, CancellationToken ct)
    {
        var ok = true;
        await using var cn = _connections.Create();
        cn.FireInfoMessageEventOnUserErrors = true;
        cn.InfoMessage += (_, e) =>
        {
            foreach (SqlError err in e.Errors)
            {
                if (err.Class > 10)
                {
                    ok = false;
                    foreach (var line in SqlcmdText.Error(err)) write(line);
                }
                else
                {
                    write(err.Message);
                }
            }
        };
        try
        {
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(statement, cn) { CommandTimeout = 0 };
            AddParameters(cmd, parameters);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            do
            {
                if (reader.FieldCount == 0) continue;
                if (headers)
                {
                    var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                    write(string.Join(' ', names));
                    write(string.Join(' ', names.Select(n => new string('-', n.Length))));
                }
                while (await reader.ReadAsync(ct))
                {
                    write(string.Join(' ', Enumerable.Range(0, reader.FieldCount).Select(i => SqlcmdText.Value(reader, i))));
                }
            } while (await reader.NextResultAsync(ct));
        }
        catch (SqlException ex)
        {
            ok = false;
            foreach (SqlError err in ex.Errors)
            {
                foreach (var line in SqlcmdText.Error(err)) write(line);
            }
        }
        return ok;
    }

    /// <summary>for /f over sqlcmd -h -1 -W: the last non-empty output line, leading blanks removed.</summary>
    public async Task<string?> QueryLastLineAsync(string statement, object? parameters, CancellationToken ct)
    {
        var lines = new List<string>();
        await ExecuteSqlAsync("SET NOCOUNT ON; " + statement, parameters, lines.Add, false, ct);
        return lines.Select(l => l.TrimStart(' ', '\t')).LastOrDefault(l => l.Length > 0);
    }

    private static void AddParameters(SqlCommand cmd, object? parameters)
    {
        switch (parameters)
        {
            case null:
                return;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                foreach (var (k, v) in pairs) cmd.Parameters.AddWithValue(k.StartsWith('@') ? k : "@" + k, v ?? DBNull.Value);
                return;
            default:
                foreach (var p in parameters.GetType().GetProperties())
                {
                    cmd.Parameters.AddWithValue("@" + p.Name, p.GetValue(parameters) ?? DBNull.Value);
                }
                return;
        }
    }

    /// <summary>cmd.exe %DATE% (the host's short date; en-US shows the day name first: 'Mon 10/12/2026').</summary>
    public static string CmdDate(DateTime d) =>
        CultureInfo.CurrentCulture.Name == "en-US"
            ? d.ToString("ddd MM/dd/yyyy", CultureInfo.CurrentCulture)
            : d.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern, CultureInfo.CurrentCulture);

    /// <summary>cmd.exe %TIME%: H:mm:ss.ff with a blank before a one-digit hour.</summary>
    public static string CmdTime(DateTime d) =>
        d.Hour.ToString(CultureInfo.InvariantCulture).PadLeft(2) + d.ToString(":mm:ss.ff", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[0-9]{8}$")]
    private static partial Regex PeriodEnd();
}
