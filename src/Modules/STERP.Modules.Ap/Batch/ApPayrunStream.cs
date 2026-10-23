using Sterp.Platform.Hosting.Jobs;

namespace Sterp.Modules.Ap.Batch;

/// <summary>
/// PAYRUN - the supplier payment run stream (jobs/PAYRUN.cmd:17-68; FR-STERP-AP-046, spec 4.11) on the PLATFORM stream engine:
/// SETENV (defaults CO=STC, USER=OPS, MODE=PREVIEW; the stream's own core.JobRun row), RESTART jump, STEP010 (locate the
/// proposal when RUN is omitted, @SQL display), STEP020 approval gate (APPROVED/RELEASED continue, PAID ends with MAXCC 4,
/// anything else MAXCC 8 and ABEND; the APPAYAPR card is only written to SYSOUT), STEP030 APBACS, STEP040 APREMIT, ABEND
/// ('NO PAYMENT FILE HAS BEEN RELEASED') and EOJ through JOBEND. The inline SQL runs as fixed parameterised statements with the
/// legacy predicates (contracts 6.2, D-PLAT-08).
/// </summary>
internal static class ApPayrunStream
{
    private const string ShowProposal =
        "SELECT ProposalRef, Status, PaymentMethod, PayDate, CashPrincipalGBP FROM ap.PaymentProposal WHERE CompanyCode=@co AND ProposalRef=@run;";

    public static JobDefinition Definition => new()
    {
        JobCode = "PAYRUN",
        Slice = "STERP-AP",
        SubmitOperationId = "ap_submitPayrunRun",
        ParameterOrder = ["CO", "RUN", "MODE", "USER", "RESTART"],
        Stream = StreamScript.Create("PAYRUN")
            // :STEP010 - locate the proposal and show it on the job log
            .Block("STEP010", async s =>
            {
                if (!s.IsDefined("RUN"))
                {
                    var located = await s.QueryLastLineAsync(
                        "SELECT TOP 1 ProposalRef FROM ap.PaymentProposal WHERE CompanyCode=@co ORDER BY CASE WHEN Status IN ('APPROVED','RELEASED') THEN 0 ELSE 1 END, ProposalId DESC;",
                        new { co = s.Var("CO") });
                    if (located is not null) s.Set("RUN", located);
                }
                await s.RunSqlStepAsync("STEP010",
                    $"SELECT ProposalRef, Status, PaymentMethod, PayDate, CashPrincipalGBP FROM ap.PaymentProposal WHERE CompanyCode='{s.Var("CO")}' AND ProposalRef='{s.Var("RUN")}';",
                    ShowProposal, new { co = s.Var("CO"), run = s.Var("RUN") });
                return StreamFlow.GateAbend(s);
            }, new StreamStepInfo("STEP010", StreamScript.SqlProgram))
            // :STEP020 - approval gate, COND CODE 8 if the proposal is not approved
            .Block("STEP020", async s =>
            {
                var run = s.Var("RUN");
                var payok = await s.QueryLastLineAsync(
                    "SELECT TOP 1 Status FROM ap.PaymentProposal WHERE CompanyCode=@co AND ProposalRef=@run;",
                    new { co = s.Var("CO"), run }) ?? "";
                s.Sysout($"//STEP020  EXEC PGM=APPAYAPR,PARM='{run} STATUS={payok}'");
                s.Console($"STERP-I-0002 STEP020 approval check {run} status {payok}");
                if (Is(payok, "APPROVED") || Is(payok, "RELEASED")) return StreamFlow.Goto("STEP030");
                if (Is(payok, "PAID")) return StreamFlow.Goto("PAIDALREADY");
                s.Sysout($"//*  PROPOSAL {run} IS '{payok}' - NOT APPROVED - COND CODE 0008");
                s.Console($"STERP-E-0008 STEP020 proposal {run} is not approved ({payok})");
                s.MaxCC = 8;
                return StreamFlow.Abend;
            })
            // :PAIDALREADY
            .Block("PAIDALREADY", s =>
            {
                s.Sysout($"//*  PROPOSAL {s.Var("RUN")} HAS ALREADY BEEN PAID - NOTHING TO DO");
                s.MaxCC = 4;
                return Task.FromResult(StreamFlow.Eoj);
            })
            // :STEP030 - Bacs Standard 18 payment file (AP-015)
            .Step("STEP030", "APBACS", s => $"CO={s.Var("CO")} RUN={s.Var("RUN")} MODE={s.Var("MODE")} USER={s.Var("USER")}")
            // :STEP040 - remittance advices (AP-016)
            .Step("STEP040", "APREMIT", s => $"CO={s.Var("CO")} RUN={s.Var("RUN")} USER={s.Var("USER")}")
            .Abend(_ => ["//*  STREAM TERMINATED - NO PAYMENT FILE HAS BEEN RELEASED"])
            .Build(),
    };

    /// <summary>cmd <c>if /i "%PAYOK%"=="VALUE"</c>.</summary>
    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
