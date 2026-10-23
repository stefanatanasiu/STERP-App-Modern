using Sterp.Platform.Hosting.Jobs;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// The FIN-owned job streams (STERP-FIN.md 7.3; contracts 8.4), written block by block as jobs\&lt;STREAM&gt;.cmd and run by
/// the PLATFORM stream engine (SETENV prologue, RESTART jump, RUNSTEP, JOBEND). They are addressed as JOB.&lt;STREAM&gt; because
/// the program YEAREND and the stream YEAREND share a name (D-FIN-05). A stream answers 501 API0501 while a step program
/// belongs to an undelivered slice (MONTHEND: CSTINVAL/STERP-CST, ARRECON/STERP-AR, RECONCTL/STERP-XMD; NIGHTLY: AGEDDEBT/
/// STERP-AR, APAGED and APVAT6M/STERP-AP) and VATRET stays 501 because its STEP010 program has no source (SME-001, D2).
/// </summary>
internal static class FinStreams
{
    public static IReadOnlyList<JobDefinition> All { get; } =
    [
        Stream("MONTHEND", "fin_submitMonthendStreamRun", Monthend(), "CO", "USER", "MODE", "ASAT", "YEAR", "PERIOD", "RESTART"),
        Stream("NIGHTLY", "fin_submitNightlyStreamRun", Nightly(), "CO", "USER", "MODE", "ASAT", "RESTART"),
        Stream("VATRET", "fin_submitVatretStreamRun", Vatret(), "CO", "USER", "MODE", "ASAT", "VATPER", "RESTART"),
        Stream("YEAREND", "fin_submitYearendStreamRun", Yearend(), "CO", "USER", "MODE", "ASAT", "YEAR", "RESTART"),
    ];

    private static JobDefinition Stream(string name, string operationId, StreamScript script, params string[] parameterOrder) => new()
    {
        JobCode = "JOB." + name,
        ProgramName = name,
        Slice = FinJobs.Slice,
        SubmitOperationId = operationId,
        ParameterOrder = parameterOrder,
        Stream = script,
    };

    /// <summary>jobs\MONTHEND.cmd: the period-end stream (STEP005 .. STEP080).</summary>
    private static StreamScript Monthend() => StreamScript.Create("MONTHEND")
        .SqlStep("STEP005",
            s => $"SELECT CompanyCode, FiscalYear, PeriodNo, Status FROM fin.Period WHERE CompanyCode='{s.Var("CO")}' AND FiscalYear={s.Var("YEAR")} AND PeriodNo={s.Var("PERIOD")};",
            "SELECT CompanyCode, FiscalYear, PeriodNo, Status FROM fin.Period WHERE CompanyCode=@co AND FiscalYear=@year AND PeriodNo=@period;",
            s => new { co = s.Var("CO"), year = s.Var("YEAR"), period = s.Var("PERIOD") })
        .Step("STEP010", "REVALRUN", s => $"CO={s.Var("CO")} ASAT={s.Var("ASAT")} MODE={s.Var("MODE")} USER={s.Var("USER")}")
        .Step("STEP020", "RECURJNL",
            s => $"CO={s.Var("CO")} YEAR={s.Var("YEAR")} PERIOD={s.Var("PERIOD")} ASAT={s.Var("PEREND")} MODE={s.Var("MODE")} USER={s.Var("USER")}")
        .Step("STEP030", "CSTINVAL", s => $"CO={s.Var("CO")} ASAT={s.Var("ASAT")} DETAIL=SUMMARY MODE={s.Var("MODE")} USER={s.Var("USER")}")
        .Step("STEP040", "ARRECON", s => $"CO={s.Var("CO")} PERIOD={s.Var("YYYYPP")} USER={s.Var("USER")}")
        .Step("STEP050", "RECONCTL", s => $"CO={s.Var("CO")} ASAT={s.Var("PEREND")} MODE={s.Var("MODE")} USER={s.Var("USER")}")
        .Step("STEP060", "TBRPT",
            s => $"CO={s.Var("CO")} YEAR={s.Var("YEAR")} PERFROM={s.Var("PERIOD")} PERTO={s.Var("PERIOD")} ZEROS=N USER={s.Var("USER")}")
        .Step("STEP070", "PLRPT", s => $"CO={s.Var("CO")} YEAR={s.Var("YEAR")} PERFROM=1 PERTO={s.Var("PERIOD")} COMPARE=BUDGET USER={s.Var("USER")}")
        .Step("STEP080", "BSRPT", s => $"CO={s.Var("CO")} YEAR={s.Var("YEAR")} PERIOD={s.Var("PERIOD")} COMPARE=PRIOR USER={s.Var("USER")}")
        .Abend(s => [StreamScript.TerminatedLine(s), "//*  CORRECT THE FAULT AND RESTART AT THE FAILING STEP"])
        .Build();

    /// <summary>jobs\NIGHTLY.cmd: the nightly service stream (STEP010 .. STEP060).</summary>
    private static StreamScript Nightly() => StreamScript.Create("NIGHTLY")
        .SqlStep("STEP010", _ => "EXEC core.usp_AgeExceptions;", "EXEC core.usp_AgeExceptions;")
        .Step("STEP020", "RECURJNL",
            s => $"CO={s.Var("CO")} YEAR={s.Var("YEAR")} PERIOD={s.Var("PERIOD")} MODE={s.Var("MODE")} ASAT={s.Var("ASAT")} USER={s.Var("USER")}")
        .Step("STEP030", "AGEDDEBT", s => $"CO={s.Var("CO")} ASAT={s.Var("ASAT")} USER={s.Var("USER")}")
        .Step("STEP040", "APAGED", s => $"CO={s.Var("CO")} ASAT={s.Var("ASAT")} USER={s.Var("USER")}")
        .Step("STEP050", "APVAT6M", s => $"CO={s.Var("CO")} ASAT={s.Var("ASAT")} MODE={s.Var("MODE")} USER={s.Var("USER")}")
        .SqlStep("STEP060", _ => "EXEC core.usp_CheckFxStaleness;", "EXEC core.usp_CheckFxStaleness;")
        .Build();

    /// <summary>jobs\VATRET.cmd: STEP005 finds the oldest open VAT period when VATPER is not given; STEP010 VATRET has no source.</summary>
    private static StreamScript Vatret() => StreamScript.Create("VATRET")
        .Block("STEP005", async s =>
        {
            if (!s.IsDefined("VATPER"))
            {
                s.Set("VATPER", await s.QueryLastLineAsync(
                    "SELECT TOP 1 VatPeriodKey FROM fin.VatPeriod WHERE CompanyCode=@co AND Status IN ('OPEN','PREPARING') ORDER BY EndDate;",
                    new { co = s.Var("CO") }));
            }
            s.Sysout("//*  VAT PERIOD " + s.Var("VATPER"));
            await s.RunSqlStepAsync("STEP005",
                $"SELECT VatPeriodKey, StartDate, EndDate, FilingDueDate, Status FROM fin.VatPeriod WHERE CompanyCode='{s.Var("CO")}' AND VatPeriodKey='{s.Var("VATPER")}';",
                "SELECT VatPeriodKey, StartDate, EndDate, FilingDueDate, Status FROM fin.VatPeriod WHERE CompanyCode=@co AND VatPeriodKey=@vatper;",
                new { co = s.Var("CO"), vatper = s.Var("VATPER") });
            return StreamFlow.GateAbend(s);
        }, new StreamStepInfo("STEP005", StreamScript.SqlProgram))
        .Step("STEP010", "VATRET", s => $"CO={s.Var("CO")} VATPER={s.Var("VATPER")} MODE={s.Var("MODE")} USER={s.Var("USER")}")
        .Step("STEP020", "MTDJSON", s => $"CO={s.Var("CO")} VATPER={s.Var("VATPER")} USER={s.Var("USER")}")
        .Build();

    /// <summary>jobs\YEAREND.cmd: the annual close (STEP005 .. STEP050); every program is FIN, so it runs from W5.</summary>
    private static StreamScript Yearend() => StreamScript.Create("YEAREND")
        .SqlStep("STEP005",
            s => $"SELECT Status, COUNT(*) AS Periods FROM fin.Period WHERE CompanyCode='{s.Var("CO")}' AND FiscalYear={s.Var("YEAR")} GROUP BY Status;",
            "SELECT Status, COUNT(*) AS Periods FROM fin.Period WHERE CompanyCode=@co AND FiscalYear=@year GROUP BY Status;",
            s => new { co = s.Var("CO"), year = s.Var("YEAR") })
        .Step("STEP010", "TBRPT", s => $"CO={s.Var("CO")} YEAR={s.Var("YEAR")} PERFROM=1 PERTO=12 ZEROS=Y USER={s.Var("USER")}")
        .Step("STEP020", "PLRPT", s => $"CO={s.Var("CO")} YEAR={s.Var("YEAR")} PERFROM=1 PERTO=12 COMPARE=PRIOR USER={s.Var("USER")}")
        .Step("STEP030", "BSRPT", s => $"CO={s.Var("CO")} YEAR={s.Var("YEAR")} PERIOD=12 COMPARE=PRIOR USER={s.Var("USER")}")
        .Step("STEP040", "YEAREND", s => $"CO={s.Var("CO")} YEAR={s.Var("YEAR")} MODE={s.Var("MODE")} USER={s.Var("USER")}")
        .SqlStep("STEP050",
            s => $"SELECT FiscalYear, COUNT(*) AS Periods, SUM(CASE WHEN Status='OPEN' THEN 1 ELSE 0 END) AS OpenPeriods FROM fin.Period WHERE CompanyCode='{s.Var("CO")}' AND FiscalYear>={s.Var("YEAR")} GROUP BY FiscalYear;",
            "SELECT FiscalYear, COUNT(*) AS Periods, SUM(CASE WHEN Status='OPEN' THEN 1 ELSE 0 END) AS OpenPeriods FROM fin.Period WHERE CompanyCode=@co AND FiscalYear>=@year GROUP BY FiscalYear;",
            s => new { co = s.Var("CO"), year = s.Var("YEAR") })
        .Abend(s => [StreamScript.TerminatedLine(s), "//*  THE YEAR IS NOT CLOSED - CORRECT AND RESTART THE STEP"])
        .Build();
}
