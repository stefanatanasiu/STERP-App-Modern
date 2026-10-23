using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Jobs;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Xmd.Jobs;

/// <summary>
/// The XMD job catalogue (STERP-XMD.md section 7.3): the eight batch programs and the two job streams, with their slice
/// submission operations.
/// </summary>
internal static class XmdJobs
{
    public const string Slice = "STERP-XMD";

    public static IReadOnlyList<JobDefinition> All { get; } =
    [
        Program("RECONCTL", "xmd_submitReconctlRun", ["CO", "ASAT", "MODE", "USER", "TOL", "SITE"], ReconctlProgram.RunAsync),
        Program("EXCPAGE", "xmd_submitExcpageRun", ["CO", "ASAT", "MODE", "USER", "OWNER", "DAYS", "SITE"], ExcpageProgram.RunAsync),
        Program("OBLIGREG", "xmd_submitObligregRun", ["CO", "ASAT", "MODE", "USER", "DAYS", "SITE"], ObligregProgram.RunAsync),
        Program("KPIOTIF", "xmd_submitKpiotifRun", ["CO", "FROM", "TO", "USER", "SITE", "DETAIL", "ASAT"], KpiotifProgram.RunAsync),
        Program("CHAINRPT", "xmd_submitChainrptRun", ["CO", "TYPE", "REF", "USER", "SITE", "ASAT"], ChainrptProgram.RunAsync),
        Program("CONVLOAD", "xmd_submitConvloadRun", ["CO", "FILE", "CUTOVER", "MODE", "USER", "ASAT", "SITE"], ConvloadProgram.RunAsync),
        Program("CASHFCST", "xmd_submitCashfcstRun", ["CO", "BANK", "FROM", "TO", "SCENARIO", "USER", "MODE", "ASAT"], CashfcstProgram.RunAsync),
        Program("VATAUDIT", "xmd_submitVatauditRun", ["CO", "VATPERIOD", "PERIOD", "MODE", "USER", "TOL"], VatauditProgram.RunAsync,
            preCheck: VatauditProgram.PreCheckAsync),
        XmdStreams.Xmdclose,
        XmdStreams.Xmdconv,
    ];

    /// <summary>A suite program: its own JOBLOG START as the first registration step of the run (XMDBATPR 9100).</summary>
    private static JobDefinition Program(string code, string op, string[] order, Func<JobRunContext, Task<int>> run,
        Func<JobPreCheckContext, JobRegistration?>? selfRegistration = null,
        Func<JobPreCheckContext, Task<LegacyResult?>>? preCheck = null) => new()
    {
        JobCode = code,
        Slice = Slice,
        SubmitOperationId = op,
        ParameterOrder = order,
        PreCheck = preCheck,
        SelfRegistration = selfRegistration ?? (pre => JoblogStart(pre, code)),
        Execute = run,
    };

    /// <summary>
    /// XMDBATPR 9100-JOB-START: JOBLOG START with job name = the program (RPT-REPORT-ID), company XB-CO and user XB-USER after
    /// 9500-STD-ARGS, no site, arguments = the command line in X(200); JOBLOG stores blank values as NULL.
    /// </summary>
    public static JobRegistration JoblogStart(JobPreCheckContext pre, string job)
    {
        var a = BatchSuite.StandardArgs(BatchSuiteKind.Xmd, pre.Args, DateTime.Now);
        return new JobRegistration(job, Blank(a.Co), Blank(a.User), null, Blank(BatchSuite.JobArguments(pre.CommandLine)));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.TrimEnd(' ');
}

/// <summary>
/// XMD job submissions (STERP-XMD.md section 7.3, D-XMD-04/05): keys upper-cased, values trimmed, blank values dropped;
/// USER is always the effective STERP user; CO defaults to X-STERP-Company and must be in the caller's access list; the
/// command line lists the ParamSpec keys first, then any other keys in request order. The run is registered once through
/// the SLICE entry point (contracts 8.3: the program's own JOBLOG row) and that row is the 202 jobRunId; a PreCheck refusal
/// (VATAUDIT without a period) is a 422 with nothing registered; the double registration of SME-010 belongs to the catalogue
/// launch (platform_launchCatalogueReport). A stream registers once, as SETENV did.
/// </summary>
internal static class XmdJobSubmission
{
    public static void Map(OperationRegistry ops, IEnumerable<JobDefinition> definitions)
    {
        foreach (var d in definitions)
        {
            var definition = d;
            ops.Handle(d.SubmitOperationId!, ctx => SubmitAsync(ctx, definition));
        }
    }

    public static async Task<IResult> SubmitAsync(OperationContext ctx, JobDefinition d)
    {
        var body = await ctx.BodyObjectAsync();
        var given = new List<KeyValuePair<string, string>>();
        foreach (var (k, v) in body)
        {
            var key = k.Trim().ToUpperInvariant();
            if (key.Length == 0 || given.Any(g => g.Key == key)) continue;
            given.Add(new(key, (Text(v) ?? "").Trim()));
        }

        var co = given.FirstOrDefault(g => g.Key == "CO").Value ?? "";
        if (co.Length == 0)
        {
            Set(given, "CO", ctx.Session.CompanyCode);
        }
        else if (!ctx.Session.User.HasCompany(co))
        {
            return ctx.Forbidden(ApiReasons.CompanySiteNotAllowed, $"{ctx.Session.UserCode} has no access to {co.ToUpperInvariant()}.");
        }
        Set(given, "USER", ctx.Session.UserCode);

        var ordered = new List<KeyValuePair<string, string?>>();
        foreach (var key in d.ParameterOrder)
        {
            var hit = given.FindIndex(g => g.Key == key);
            if (hit < 0) continue;
            ordered.Add(new(given[hit].Key, given[hit].Value));
            given.RemoveAt(hit);
        }
        ordered.AddRange(given.Select(g => new KeyValuePair<string, string?>(g.Key, g.Value)));
        var cmd = CommandLineArgs.Build(ordered);

        var parameters = new JsonObject();
        foreach (var (k, v) in ordered.Where(p => !string.IsNullOrWhiteSpace(p.Value))) parameters[k] = v;
        var accepted = await ctx.Get<JobService>().SubmitAsync(
            new JobSubmitRequest(JobEntryPoint.Slice, d, cmd, parameters), ctx.Session, ctx.Services, ctx.Aborted);
        return JobEndpoints.Accepted(ctx, accepted);
    }

    private static void Set(List<KeyValuePair<string, string>> pairs, string key, string value)
    {
        var i = pairs.FindIndex(p => p.Key == key);
        if (i >= 0) pairs[i] = new(key, value);
        else pairs.Add(new(key, value));
    }

    private static string? Text(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Y" : "N",
        _ => n.ToJsonString(),
    };

}
