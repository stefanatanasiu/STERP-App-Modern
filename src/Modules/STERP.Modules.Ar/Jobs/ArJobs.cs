using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.Ar.Jobs;

/// <summary>The AR batch programs and job streams (STERP-AR.md section 7.4).</summary>
public static class ArJobs
{
    public static IReadOnlyList<JobDefinition> Programs() =>
    [
        AgedDebtJob.Definition,
        ArreconJob.Definition,
        DunningJob.Definition,
        StatementJob.Definition,
        DdcollJob.Definition,
        EinvublJob.Definition,
    ];

    /// <summary>
    /// jobs\DDCOLL.cmd (FR-STERP-AR-066): job code JOB.DDCOLL (decision D-AR-05; DDCOLL is the program), STEP010 DDCOLL with
    /// CO, ASAT, MODE, USER (+ SUN when given), STEP020 the @SQL evidence query.
    /// </summary>
    public static JobDefinition DirectDebitStream => new()
    {
        JobCode = "JOB.DDCOLL",
        Slice = ArBatch.Slice,
        SubmitOperationId = "ar_runDirectDebitStream",
        ParameterOrder = ["CO", "ASAT", "SUN", "MODE", "USER", "RESTART"],
        Stream = StreamScript.Create("DDCOLL")
            .Block("STEP010", async s =>
            {
                var parm = $"CO={s.Var("CO")} ASAT={s.Var("ASAT")} MODE={s.Var("MODE")} USER={s.Var("USER")}";
                if (s.Var("SUN").Length > 0) parm += $" SUN={s.Var("SUN")}";
                await s.RunStepAsync("STEP010", "DDCOLL", parm);
                return StreamFlow.GateAbend(s);
            }, new StreamStepInfo("STEP010", "DDCOLL"))
            .SqlStep("STEP020",
                s => $"SELECT Status, COUNT(*) AS Collections, SUM(AmountGBP) AS GBP FROM ar.DirectDebitCollection WHERE CompanyCode='{s.Var("CO")}' GROUP BY Status;",
                "SELECT Status, COUNT(*) AS Collections, SUM(AmountGBP) AS GBP FROM ar.DirectDebitCollection WHERE CompanyCode=@co GROUP BY Status;",
                s => new { co = s.Var("CO") })
            .Build(),
    };

    /// <summary>
    /// jobs\STMTS.cmd (FR-STERP-AR-067): STYLE default OPENITEM and STAGE default ALL (set after the RESTART jump), STEP010
    /// STATEMNT, STEP020 DUNNING, STEP030 the @SQL count of open AR exceptions.
    /// </summary>
    public static JobDefinition StatementsStream => new()
    {
        JobCode = "STMTS",
        Slice = ArBatch.Slice,
        SubmitOperationId = "ar_runStatementsStream",
        ParameterOrder = ["CO", "ASAT", "STYLE", "STAGE", "MODE", "USER", "RESTART"],
        Stream = StreamScript.Create("STMTS")
            .Block("DEFAULTS", s =>
            {
                if (s.Var("STYLE").Length == 0) s.Set("STYLE", "OPENITEM");
                if (s.Var("STAGE").Length == 0) s.Set("STAGE", "ALL");
                return Task.FromResult(StreamFlow.Next);
            })
            .Step("STEP010", "STATEMNT", s => $"CO={s.Var("CO")} ASAT={s.Var("ASAT")} STYLE={s.Var("STYLE")} USER={s.Var("USER")}")
            .Step("STEP020", "DUNNING", s => $"CO={s.Var("CO")} ASAT={s.Var("ASAT")} STAGE={s.Var("STAGE")} MODE={s.Var("MODE")} USER={s.Var("USER")}")
            .SqlStep("STEP030",
                s => $"SELECT Severity, Category, COUNT(*) AS Items FROM core.ExceptionItem WHERE CompanyCode='{s.Var("CO")}' AND Module='AR' AND Status='OPEN' GROUP BY Severity, Category;",
                "SELECT Severity, Category, COUNT(*) AS Items FROM core.ExceptionItem WHERE CompanyCode=@co AND Module='AR' AND Status='OPEN' GROUP BY Severity, Category;",
                s => new { co = s.Var("CO") })
            .Build(),
    };

    public static void Map(JobCatalog jobs)
    {
        foreach (var d in Programs()) jobs.Add(d);
        jobs.Add(DirectDebitStream);
        jobs.Add(StatementsStream);
    }

    /// <summary>The submissions (POST /api/ar/v1/jobs/{jobCode}/runs) with the CO header default (STERP-AR.md 7.4).</summary>
    public static void MapSubmissions(OperationRegistry operations)
    {
        foreach (var d in Programs().Append(DirectDebitStream).Append(StatementsStream).Where(d => d.SubmitOperationId is not null))
        {
            var definition = d;
            operations.Handle(d.SubmitOperationId!, ctx => ArBatch.SubmitAsync(ctx, definition));
        }
    }
}
