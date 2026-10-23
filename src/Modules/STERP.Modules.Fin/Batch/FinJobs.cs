using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// The FIN job catalogue (STERP-FIN.md 7.3): one definition per batch program, submitted through POST
/// /api/fin/v1/jobs/{jobCode}/runs. A FIN program registers itself through JOBLOG START (FINBATPR 9100: job name = report id,
/// CA-COMPANY = WS-CO, CA-USER = WS-USER, no site, the command line as arguments) and ends through JOBLOG END (9150: WS-RC
/// as COMPLETE->OK / WARNING / FAILED, WS-ROWS-OUT, WS-SPOOL-NAME), so a slice submission registers that single row
/// (contracts 8.3). The SYSOUT of the run is recorded as its log file so the run resources can serve it.
/// </summary>
internal static class FinJobs
{
    public const string Slice = "STERP-FIN";

    public static IReadOnlyList<JobDefinition> All { get; } =
    [
        Program("TBRPT", "fin_submitTbrptRun", TbrptJob.RunAsync,
            "CO", "USER", "ASAT", "MODE", "FY", "FROM", "TO", "ZERO", "YEAR", "PERFROM", "PERTO", "ZEROS"),
        Program("PLRPT", "fin_submitPlrptRun", PlrptJob.RunAsync,
            "CO", "USER", "ASAT", "MODE", "FY", "FROM", "TO", "LAYOUT", "YEAR", "PERFROM", "PERTO", "COMPARE"),
        Program("BSRPT", "fin_submitBsrptRun", BsrptJob.RunAsync,
            "CO", "USER", "ASAT", "MODE", "YEAR", "PERIOD", "COMPARE"),
        Program("CASHBOOK", "fin_submitCashbookRun", CashbookJob.RunAsync,
            "CO", "USER", "ASAT", "MODE", "FROM", "TO", "BANK"),
        Program("BUDGRPT", "fin_submitBudgrptRun", BudgrptJob.RunAsync,
            "CO", "USER", "ASAT", "MODE", "FY", "BUDG", "VER", "CMP", "CMPVER", "FROM", "TO", "YEAR", "PERIOD", "BUDGET"),
        Program("RECURJNL", "fin_submitRecurjnlRun", RecurjnlJob.RunAsync,
            "CO", "USER", "ASAT", "MODE", "CODE", "YEAR", "PERIOD"),
        Program("REVALRUN", "fin_submitRevalrunRun", RevalrunJob.RunAsync,
            "CO", "USER", "ASAT", "MODE", "RATE"),
        Program("MTDJSON", "fin_submitMtdjsonRun", MtdjsonJob.RunAsync,
            "CO", "USER", "ASAT", "MODE", "VATPER", "VATPERIOD"),
        Program("YEAREND", "fin_submitYearendRun", YearendJob.RunAsync,
            "CO", "USER", "ASAT", "MODE", "FY", "YEAR"),
    ];

    /// <summary>A FIN batch program: the report id is the program name (RPT-REPORT-ID, JOBLOG job name and spool prefix).</summary>
    public static JobDefinition Program(string program, string operationId, Func<FinBatch, Task<int>> run, params string[] parameterOrder) => new()
    {
        JobCode = program,
        Slice = Slice,
        SubmitOperationId = operationId,
        ParameterOrder = parameterOrder,
        SelfRegistration = pre => JoblogStart(program, pre),
        Execute = ctx => run(new FinBatch(ctx)),
    };

    /// <summary>9100-JOB-START values (JOBLOG.cob:129-152: blank company, user, site or arguments are NULL).</summary>
    private static JobRegistration JoblogStart(string reportId, JobPreCheckContext pre)
    {
        var std = BatchSuite.StandardArgs(BatchSuiteKind.Fin, pre.Args, DateTime.Now);
        return new JobRegistration(reportId, Blank(std.Co), Blank(std.User), null, Blank(BatchSuite.JobArguments(pre.CommandLine)));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.TrimEnd(' ');
}
