using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Periods;

/// <summary>One CL-STEPS entry.</summary>
public sealed record CloseStep(string Code, string Name, string Status, string Blocking, decimal Difference, string Owner, string Evidence);

/// <summary>PERCLOSECA body (cobol/copy/PERCLOSECA.cpy).</summary>
public sealed class PerCloseArea
{
    public int FiscalYear { get; set; }
    public int PeriodNo { get; set; }
    public string StepCode { get; set; } = "";
    public string StepAction { get; set; } = "";
    public string EvidenceRef { get; set; } = "";
    public decimal Difference { get; set; }
    public string Reason { get; set; } = "";
    public string Reviewer { get; set; } = "";
    public string Force { get; set; } = "";

    public string PeriodName { get; set; } = "";
    public string PeriodStatus { get; set; } = "";
    public int StartDate { get; set; }
    public int EndDate { get; set; }
    public int StepCnt { get; set; }
    public int DoneCnt { get; set; }
    public int BlockingOpen { get; set; }
    public decimal UnresolvedDiff { get; set; }
    public decimal Materiality { get; set; }
    public string Readiness { get; set; } = "";
    public string ClosedBy { get; set; } = "";
    public string SignedOff { get; set; } = "N";
    public List<CloseStep> Steps { get; } = [];

    public JsonObject ToJson()
    {
        var steps = new JsonArray();
        foreach (var s in Steps)
        {
            steps.Add(new JsonObject
            {
                ["stepCode"] = s.Code,
                ["stepName"] = s.Name,
                ["status"] = s.Status,
                ["blocking"] = s.Blocking,
                ["difference"] = CobolNumeric.WithScale(s.Difference, 2),
                ["ownerRole"] = s.Owner,
                ["evidenceRef"] = s.Evidence,
            });
        }
        return new JsonObject
        {
            ["fiscalYear"] = FiscalYear,
            ["periodNo"] = PeriodNo,
            ["periodName"] = PeriodName,
            ["periodStatus"] = PeriodStatus,
            ["startDate"] = Bank.BankRecArea.CobolDateText(StartDate),
            ["endDate"] = Bank.BankRecArea.CobolDateText(EndDate),
            ["stepCnt"] = StepCnt,
            ["doneCnt"] = DoneCnt,
            ["blockingOpen"] = BlockingOpen,
            ["unresolvedDiff"] = CobolNumeric.WithScale(UnresolvedDiff, 2),
            ["materiality"] = CobolNumeric.WithScale(Materiality, 2),
            ["readiness"] = Readiness,
            ["closedBy"] = ClosedBy,
            ["signedOff"] = SignedOff,
            ["stepsListed"] = Steps.Count,
            ["steps"] = steps,
        };
    }
}

/// <summary>
/// PERCLOSE (cobol/online/fin/PERCLOSE.cob) functions STATUS, STEP, SOFT, HARD and REOPEN. No unit of work: every
/// statement autocommits; AUDITLOG after each change.
/// </summary>
public sealed class PeriodCloseProgram(ISqlRunner sql, IFinKernel kernel)
{
    private const string Program = "PERCLOSE";

    public async Task<CaHeader> RunAsync(CaRequest req, PerCloseArea cl, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-010");
        var rejected = false;
        await InitialiseAsync(req, cl, ct);
        if (!Edit(req, ca, cl)) return ca;
        try
        {
            switch (req.Function)
            {
                case "STATUS":
                    await StatusAsync(req, ca, cl);
                    break;
                case "STEP":
                    await StepAsync(req, ca, cl, ct);
                    break;
                case "SOFT":
                case "HARD":
                    await CloseAsync(req, ca, cl, ct);
                    break;
                case "REOPEN":
                    await ReopenAsync(req, ca, cl, ct);
                    break;
                default:
                    ca.Reject("CLS0009", "PERCLOSE does not support function " + req.Function.Trim());
                    rejected = true;
                    break;
            }
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
        }
        _ = rejected;
        return ca;
    }

    // 0100-INITIALISE + 0150-MATERIALITY
    private async Task InitialiseAsync(CaRequest req, PerCloseArea cl, CancellationToken ct)
    {
        cl.StepCnt = cl.DoneCnt = cl.BlockingOpen = 0;
        cl.UnresolvedDiff = 0;
        cl.Steps.Clear();
        cl.Readiness = "";
        cl.PeriodStatus = "";
        cl.SignedOff = "N";
        cl.Materiality = 1000;
        var pm = await kernel.ParmGetAsync(req.Callee("GETNUM", site: false, correlation: false), "FIN.CLOSE.MATERIALITY", 0, numeric: true, ct);
        if (pm.Found && pm.ValueNum > 0) cl.Materiality = CobolNumeric.Store(pm.ValueNum, 11, 2);
    }

    // 0200-EDIT-REQUEST
    private static bool Edit(CaRequest req, CaHeader ca, PerCloseArea cl)
    {
        if (CobolText.IsSpaces(req.Company))
        {
            ca.Reject("CLS0001", "A company is required");
            return false;
        }
        if (cl.FiscalYear == 0 || cl.PeriodNo == 0)
        {
            ca.Reject("CLS0002", "A fiscal year and period number are required");
            return false;
        }
        return true;
    }

    // 1000-STATUS
    private async Task StatusAsync(CaRequest req, CaHeader ca, PerCloseArea cl)
    {
        var sets = await sql.WithReaderAsync("EXEC fin.usp_CloseStatus @p1, @p2, @p3",
            SqlioCalls.Args([req.Company, Y4(cl.FiscalYear), P2(cl.PeriodNo)]),
            async r =>
            {
                object?[]? header = null;
                var steps = new List<object?[]>();
                if (await r.ReadAsync()) header = Values(r);
                if (header is not null && await r.NextResultAsync())
                {
                    while (steps.Count < 20 && await r.ReadAsync()) steps.Add(Values(r));
                }
                return (header, steps);
            });
        if (sets.header is null)
        {
            ca.Set(16, "CLS0003", "That accounting period is not held for the compan");
            return;
        }
        var h = sets.header;
        cl.PeriodName = Slot.Text(h[3], 20);
        cl.PeriodStatus = Slot.Text(h[6], 8);
        cl.StartDate = (int)(Math.Abs(Slot.NumValOfDate(h[4], 10)) % 100_000_000);
        cl.EndDate = (int)(Math.Abs(Slot.NumValOfDate(h[5], 10)) % 100_000_000);
        cl.StepCnt = (int)(Math.Abs(Slot.Int(h[12])) % 1000);
        cl.DoneCnt = (int)(Math.Abs(Slot.Int(h[13])) % 1000);
        cl.BlockingOpen = (int)(Math.Abs(Slot.Int(h[14])) % 1000);
        cl.UnresolvedDiff = CobolNumeric.Store(Slot.Num(h[15]), 13, 2);
        cl.Readiness = Slot.Text(h[16], 10);
        cl.ClosedBy = Slot.Text(h[9], 20);
        if (cl.StepCnt > 0 && cl.DoneCnt == cl.StepCnt) cl.SignedOff = "Y";
        foreach (var t in sets.steps)
        {
            cl.Steps.Add(new CloseStep(
                Slot.Text(t[1], 20),
                Slot.Text(t[2], 60),
                Slot.Text(t[5], 12),
                BitText(t[6]).StartsWith('1') ? "Y" : "N",
                CobolNumeric.Store(Slot.Num(t[7]), 11, 2),
                Slot.Text(t[3], 20),
                Slot.Text(t[8], 60)));
        }
        ca.Message = cl.BlockingOpen > 0
            ? "Period " + cl.PeriodName.Trim() + " is not ready to close: blocking checklist steps remain open"
            : "Period " + cl.PeriodName.Trim() + " is " + cl.PeriodStatus.Trim() + " and the checklist is complete";
    }

    private static string Y4(int v) => (v % 10000).ToString("0000", CultureInfo.InvariantCulture);

    private static string P2(int v) => (v % 100).ToString("00", CultureInfo.InvariantCulture);

    private static object?[] Values(System.Data.Common.DbDataReader r)
    {
        var v = new object?[r.FieldCount];
        for (var i = 0; i < v.Length; i++) v[i] = r.IsDBNull(i) ? null : r.GetValue(i);
        return v;
    }

    private static string BitText(object? v) => v switch
    {
        bool b => b ? "1" : "0",
        null => "",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    // 3000-STEP
    private async Task StepAsync(CaRequest req, CaHeader ca, PerCloseArea cl, CancellationToken ct)
    {
        if (!await AuthorityAsync(req, ca, "FIN.PERIOD.CLOSE", ct)) return;
        if (CobolText.IsSpaces(cl.StepCode))
        {
            ca.Reject("CLS0010", "A checklist step code is required");
            return;
        }
        if (CobolText.IsSpaces(cl.StepAction)) cl.StepAction = "DONE";
        if (cl.StepAction == "DONE" && CobolText.IsSpaces(cl.EvidenceRef))
        {
            ca.Reject("CLS0011", "Each completed close step must reference its evid");
            return;
        }
        if (cl.StepAction == "ACCEPT" && CobolText.IsSpaces(cl.Reason))
        {
            ca.Reject("CLS0012", "Accepting an unresolved difference needs a permit");
            return;
        }
        if (cl.StepAction == "DONE" && Math.Abs(cl.Difference) > cl.Materiality)
        {
            ca.ReasonCode = "CLS0013";
            ca.Message = "That step carries an unresolved difference of " + FinEdit.Money(cl.Difference) +
                         " GBP, which is material. It must be explained or formally accepted by the controller";
            ca.ReturnCode = 6;
            ca.MsgCount++;
            return;
        }
        var newStatus = cl.StepAction == "ACCEPT" ? "ACCEPTED" : cl.StepAction == "BLOCK" ? "BLOCKED" : "DONE";
        var rows = await sql.ExecAsync(
            "UPDATE fin.CloseChecklist SET Status = @p1, EvidenceRef = NULLIF(@p2,''), DifferenceGBP = @p3, " +
            "Note = NULLIF(@p4,''), CompletedBy = @p5, CompletedAt = SYSDATETIME(), ReviewedBy = NULLIF(@p6,''), " +
            "ReviewedAt = CASE WHEN @p7 <> '' THEN SYSDATETIME() END WHERE CompanyCode = @p8 AND FiscalYear = @p9 " +
            "AND PeriodNo = @p10 AND StepCode = @p11",
            newStatus, cl.EvidenceRef, cl.Difference, cl.Reason, req.User, cl.Reviewer, cl.Reviewer, req.Company,
            Y4(cl.FiscalYear), P2(cl.PeriodNo), cl.StepCode);
        if (rows == 0)
        {
            ca.Set(16, "CLS0014", "That checklist step is not held for t");
            return;
        }
        await AuditAsync(req, cl, ct);
        await StatusAsync(req, ca, cl);
    }

    // 4000-CLOSE-PERIOD
    private async Task CloseAsync(CaRequest req, CaHeader ca, PerCloseArea cl, CancellationToken ct)
    {
        if (!await AuthorityAsync(req, ca, "FIN.PERIOD.CLOSE", ct)) return;
        await StatusAsync(req, ca, cl);
        if (ca.Failed) return;
        var newStatus = req.Function == "SOFT" ? "SOFT" : "HARD";
        if (cl.PeriodStatus == "HARD")
        {
            ca.Reject("CLS0020", "That period is already hard closed");
            return;
        }
        if (newStatus == "HARD" && cl.BlockingOpen > 0 && cl.Force != "Y")
        {
            ca.ReasonCode = "CLS0021";
            ca.Message = "Hard close is blocked: blocking checklist steps are still open and " + FinEdit.Money(cl.UnresolvedDiff) +
                         " GBP of difference is unresolved";
            ca.ReturnCode = 6;
            ca.MsgCount++;
            return;
        }
        await sql.ExecAsync(
            "UPDATE fin.Period SET Status = @p1, SoftCloseAt = CASE WHEN @p2 = 'SOFT' THEN SYSDATETIME() ELSE SoftCloseAt END, " +
            "HardCloseAt = CASE WHEN @p3 = 'HARD' THEN SYSDATETIME() ELSE HardCloseAt END, ClosedBy = @p4 WHERE CompanyCode = @p5 " +
            "AND FiscalYear = @p6 AND PeriodNo = @p7",
            newStatus, newStatus, newStatus, req.User, req.Company, Y4(cl.FiscalYear),
            P2(cl.PeriodNo));
        cl.PeriodStatus = newStatus;
        cl.ClosedBy = req.User;
        await AuditAsync(req, cl, ct);
        ca.Message = newStatus == "SOFT"
            ? "Period " + cl.PeriodName.Trim() + " is soft closed. Only authorised close adjustments may now be posted into it"
            : "Period " + cl.PeriodName.Trim() + " is hard closed. Posting into it is blocked";
        if (cl.Force == "Y" && cl.BlockingOpen > 0)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "CLS0022";
            ca.MsgCount++;
        }
    }

    // 5000-REOPEN
    private async Task ReopenAsync(CaRequest req, CaHeader ca, PerCloseArea cl, CancellationToken ct)
    {
        if (!await AuthorityAsync(req, ca, "FIN.PERIOD.REOPEN", ct)) return;
        if (CobolText.IsSpaces(cl.Reason) || cl.Reason.All(c => c == '\0'))
        {
            ca.Reject("CLS0030", "Reopening a closed period requires a recorded rea");
            return;
        }
        if (CobolText.IsSpaces(cl.Reviewer))
        {
            ca.Reject("CLS0031", "Reopening requires a named approver");
            return;
        }
        var rows = await sql.ExecAsync(
            "UPDATE fin.Period SET Status = 'OPEN', ReopenedBy = @p1, ReopenedAt = SYSDATETIME(), ReopenReason = @p2 " +
            "WHERE CompanyCode = @p3 AND FiscalYear = @p4 AND PeriodNo = @p5 AND Status IN ('SOFT','HARD')",
            cl.Reviewer, cl.Reason, req.Company, Y4(cl.FiscalYear),
            P2(cl.PeriodNo));
        if (rows == 0)
        {
            ca.Set(16, "CLS0032", "That period is not closed, so there is nothing to");
            return;
        }
        cl.PeriodStatus = "OPEN";
        await AuditAsync(req, cl, ct);
        ca.ReturnCode = 4;
        ca.ReasonCode = "CLS0033";
        ca.Message = "Period reopened under the approved calendar policy. The trial balance, statements and any VAT return covering it must be reissued";
        ca.MsgCount++;
    }

    // 8100-AUTHORITY: false when refused
    private async Task<bool> AuthorityAsync(CaRequest req, CaHeader ca, string permission, CancellationToken ct)
    {
        var kh = await kernel.PermCheckAsync(req.Callee("CHECK", site: true, correlation: false), permission, ct);
        if (kh.ReturnCode <= 4) return true;
        ca.ReasonCode = kh.ReasonCode;
        ca.Message = kh.Message;
        ca.ReturnCode = kh.ReturnCode;
        ca.MsgCount++;
        return false;
    }

    // 8200-AUDIT
    private Task AuditAsync(CaRequest req, PerCloseArea cl, CancellationToken ct) =>
        kernel.AuditAsync(req.Callee("WRITE", site: false, correlation: false), new AuditEntry(
            EntityType: "fin.Period",
            EntityKey: req.Company.Trim() + "/" + cl.FiscalYear.ToString("0000", CultureInfo.InvariantCulture) + "/" +
                       cl.PeriodNo.ToString("00", CultureInfo.InvariantCulture),
            Action: req.Function,
            NewValue: cl.PeriodStatus,
            Reason: cl.Reason,
            AuthorityRef: CobolText.Fit(cl.EvidenceRef, 200)[..60].TrimEnd(),
            Source: "COBOL"), ct);
}
