using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Hosting.Jobs;

namespace Sterp.Modules.Xmd.Jobs;

/// <summary>
/// The XMD job streams (FR-STERP-XMD-029, -030): jobs\XMDCLOSE.cmd and jobs\XMDCONV.cmd written block by block for the
/// platform stream engine (SETENV prologue, RUNSTEP, JOBEND). Each block also records the XmdStreamResult of the run
/// (steps with their PARM and COND code, MAXCC, JOBEND status) as the run's JSON result.
/// </summary>
internal static class XmdStreams
{
    /// <summary>jobs\XMDCLOSE.cmd: RECONCTL, OBLIGREG and EXCPAGE at one shared cutoff.</summary>
    public static JobDefinition Xmdclose { get; } = new()
    {
        JobCode = "XMDCLOSE",
        Slice = XmdJobs.Slice,
        SubmitOperationId = "xmd_submitXmdcloseRun",
        ParameterOrder = ["CO", "ASAT", "MODE", "USER", "TOL", "DAYS", "OWNER", "RESTART"],
        Stream = StreamScript.Create("XMDCLOSE")
            .Block("STEP010", s =>
            {
                // set "RCPARM=CO=%P_CO% ASAT=%P_ASAT% MODE=%P_MODE% USER=%P_USER%" & if defined P_TOL ... TOL=%P_TOL%
                var parm = Std(s);
                if (s.IsDefined("TOL")) parm += " TOL=" + s.Var("TOL");
                return StepAsync(s, "STEP010", "RECONCTL", parm);
            }, new StreamStepInfo("STEP010", "RECONCTL"))
            .Block("STEP020", s =>
            {
                var parm = Std(s);
                if (s.IsDefined("DAYS")) parm += " DAYS=" + s.Var("DAYS");
                return StepAsync(s, "STEP020", "OBLIGREG", parm);
            }, new StreamStepInfo("STEP020", "OBLIGREG"))
            .Block("STEP030", s =>
            {
                var parm = Std(s);
                if (s.IsDefined("OWNER")) parm += " OWNER=" + s.Var("OWNER");
                if (s.IsDefined("DAYS")) parm += " DAYS=" + s.Var("DAYS");
                return StepAsync(s, "STEP030", "EXCPAGE", parm);
            }, new StreamStepInfo("STEP030", "EXCPAGE"))
            .Abend(Abend)
            .Build(),
    };

    /// <summary>jobs\XMDCONV.cmd: the four Caledon extracts through CONVLOAD, PREVIEW then (MODE=COMMIT) COMMIT, then STEP030.</summary>
    public static JobDefinition Xmdconv { get; } = new()
    {
        JobCode = "XMDCONV",
        Slice = XmdJobs.Slice,
        SubmitOperationId = "xmd_submitXmdconvRun",
        ParameterOrder = ["CO", "MODE", "USER", "CUTOVER", "RESTART"],
        Stream = StreamScript.Create("XMDCONV")
            .Setup(s =>
            {
                // if not defined P_CUTOVER set "P_CUTOVER=20260331"; set "CVTAIL=CUTOVER=%P_CUTOVER% USER=%P_USER%"
                if (!s.IsDefined("CUTOVER")) s.Set("CUTOVER", "20260331");
            })
            .Block("STEP010", s => StepAsync(s, "STEP010", "CONVLOAD", Conv(s, "CALGLTB.DAT", "PREVIEW")), new StreamStepInfo("STEP010", "CONVLOAD"))
            .Block("STEP011", s => StepAsync(s, "STEP011", "CONVLOAD", Conv(s, "CALAROPN.DAT", "PREVIEW")), new StreamStepInfo("STEP011", "CONVLOAD"))
            .Block("STEP012", s => StepAsync(s, "STEP012", "CONVLOAD", Conv(s, "CALAPOPN.DAT", "PREVIEW")), new StreamStepInfo("STEP012", "CONVLOAD"))
            .Block("STEP013", async s =>
            {
                var flow = await StepAsync(s, "STEP013", "CONVLOAD", Conv(s, "CALSTOCK.DAT", "PREVIEW"));
                if (flow.Label is not null) return flow;
                if (!string.Equals(s.Var("MODE"), "COMMIT", StringComparison.OrdinalIgnoreCase))
                {
                    s.Sysout("//*  MODE=PREVIEW - THE COMMIT PASS IS BYPASSED");
                    return StreamFlow.Eoj;
                }
                return StreamFlow.Next;
            }, new StreamStepInfo("STEP013", "CONVLOAD"))
            .Block("STEP020", s => StepAsync(s, "STEP020", "CONVLOAD", Conv(s, "CALGLTB.DAT", "COMMIT")), new StreamStepInfo("STEP020", "CONVLOAD"))
            .Block("STEP021", s => StepAsync(s, "STEP021", "CONVLOAD", Conv(s, "CALAROPN.DAT", "COMMIT")), new StreamStepInfo("STEP021", "CONVLOAD"))
            .Block("STEP022", s => StepAsync(s, "STEP022", "CONVLOAD", Conv(s, "CALAPOPN.DAT", "COMMIT")), new StreamStepInfo("STEP022", "CONVLOAD"))
            .Block("STEP023", s => StepAsync(s, "STEP023", "CONVLOAD", Conv(s, "CALSTOCK.DAT", "COMMIT")), new StreamStepInfo("STEP023", "CONVLOAD"))
            .Block("STEP030", async s =>
            {
                // the statement as the script wrote it (D-XMD-11); the company is bound as a parameter (D-PLAT-08)
                await s.RunSqlStepAsync("STEP030",
                    "SELECT SourceFile, Outcome, COUNT(*) AS Records FROM stg.ImportRow WHERE CompanyCode='" + s.Var("CO") +
                    "' GROUP BY SourceFile, Outcome ORDER BY SourceFile, Outcome;",
                    "SELECT SourceFile, Outcome, COUNT(*) AS Records FROM stg.ImportRow WHERE CompanyCode=@co GROUP BY SourceFile, Outcome ORDER BY SourceFile, Outcome;",
                    new { co = s.Var("CO") });
                SaveResult(s, false);
                return StreamFlow.GateAbend(s);
            }, new StreamStepInfo("STEP030", StreamScript.SqlProgram))
            .Abend(Abend)
            .Build(),
    };

    /// <summary>"CO=%P_CO% ASAT=%P_ASAT% MODE=%P_MODE% USER=%P_USER%".</summary>
    private static string Std(StreamContext s) =>
        "CO=" + s.Var("CO") + " ASAT=" + s.Var("ASAT") + " MODE=" + s.Var("MODE") + " USER=" + s.Var("USER");

    /// <summary>"CO=%P_CO% FILE=data\legacy\&lt;file&gt; MODE=&lt;mode&gt; %CVTAIL%".</summary>
    private static string Conv(StreamContext s, string file, string mode) =>
        "CO=" + s.Var("CO") + " FILE=data\\legacy\\" + file + " MODE=" + mode + " CUTOVER=" + s.Var("CUTOVER") + " USER=" + s.Var("USER");

    /// <summary>call RUNSTEP.cmd STEP PGM "PARM" then if %LASTCC% GEQ 8 goto ABEND.</summary>
    private static async Task<StreamFlow> StepAsync(StreamContext s, string step, string program, string parm)
    {
        await s.RunStepAsync(step, program, parm);
        SaveResult(s, false);
        return StreamFlow.GateAbend(s);
    }

    /// <summary>:ABEND - echo //*  STREAM TERMINATED BY COND CODE %LASTCC%.</summary>
    private static IEnumerable<string> Abend(StreamContext s)
    {
        SaveResult(s, true);
        return [StreamScript.TerminatedLine(s)];
    }

    /// <summary>The XmdStreamResult of the run so far (MAXCC no longer changes after the last step).</summary>
    private static void SaveResult(StreamContext s, bool terminated)
    {
        if (s.JobRunId is null) return;
        var jobs = s.Services.GetRequiredService<JobService>();
        var m = jobs.Manifests.Load(s.JobRunId.Value);
        if (m is null) return;
        var streamRows = new HashSet<long> { s.JobRunId.Value };
        if (m.OwnJobRunId is { } own) streamRows.Add(own);
        if (m.LauncherJobRunId is { } launcher) streamRows.Add(launcher);
        var stepRuns = new Queue<long>(m.RelatedJobRunIds.Where(id => !streamRows.Contains(id)));
        var steps = new JsonArray();
        foreach (var st in m.Steps)
        {
            long? runId = null;
            if (!string.Equals(st.Program, StreamScript.SqlProgram, StringComparison.OrdinalIgnoreCase) && stepRuns.Count > 0) runId = stepRuns.Dequeue();
            steps.Add(new JsonObject
            {
                ["step"] = st.StepName,
                ["program"] = st.Program,
                ["parameters"] = st.Parm,
                ["conditionCode"] = st.CondCode,
                ["jobRunId"] = runId,
            });
        }
        var result = new JsonObject
        {
            ["jobName"] = s.JobName,
            ["steps"] = steps,
            ["maxcc"] = s.MaxCC,
            ["status"] = JobService.JobendStatus(s.MaxCC),
            ["terminatedByCondCode"] = terminated,
        };
        foreach (var id in streamRows) jobs.Manifests.SaveResult(id, result);
    }
}
