using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Xmd.Services;

/// <summary>One CT-STEP occurrence of CHAINTRCCA (cobol/copy/CHAINTRCCA.cpy:31-41).</summary>
public sealed class ChainTraceStep
{
    /// <summary>CT-SEQ 9(2).</summary>
    public int Seq { get; set; }

    /// <summary>CT-STAGE X(24).</summary>
    public string Stage { get; set; } = "";

    /// <summary>CT-DOC-T X(12).</summary>
    public string DocType { get; set; } = "";

    /// <summary>CT-REF X(40).</summary>
    public string Reference { get; set; } = "";

    /// <summary>CT-DATE 9(8) yyyymmdd (0 = none).</summary>
    public int Date { get; set; }

    /// <summary>CT-STATUS X(12).</summary>
    public string Status { get; set; } = "";

    /// <summary>CT-QTY S9(11)V9(3).</summary>
    public decimal Quantity { get; set; }

    /// <summary>CT-VALUE S9(13)V99.</summary>
    public decimal Value { get; set; }

    /// <summary>CT-KIND X(10).</summary>
    public string Kind { get; set; } = "";

    /// <summary>CT-NOTE X(60).</summary>
    public string Note { get; set; } = "";
}

/// <summary>CHAINTRCCA: the STCAHDR company plus the chain trace body (CHAINTRCCA.cpy:16-41).</summary>
public sealed class ChainTraceArea
{
    public const int MaxStepSlots = 24;

    /// <summary>CA-COMPANY X(10).</summary>
    public string Company { get; set; } = "";

    /// <summary>CA-FUNCTION (TRACE).</summary>
    public string Function { get; set; } = "TRACE";

    /// <summary>CT-DOC-TYPE X(12) (upper-cased, then overwritten with the list document type once a head step is found).</summary>
    public string DocType { get; set; } = "";

    /// <summary>CT-DOC-REF X(40).</summary>
    public string DocRef { get; set; } = "";

    /// <summary>CT-CHAIN-TYPE X(12).</summary>
    public string ChainType { get; set; } = "";

    /// <summary>CT-CHAIN-NAME X(40).</summary>
    public string ChainName { get; set; } = "";

    /// <summary>CT-PARTY X(20).</summary>
    public string Party { get; set; } = "";

    /// <summary>CT-STEP-CNT S9(9) COMP-5.</summary>
    public int StepCount { get; set; }

    /// <summary>CT-MAX-STEPS S9(9) COMP-5 (CHAINTRC forces 24).</summary>
    public int MaxSteps { get; set; } = 24;

    public string Truncated { get; set; } = "N";
    public string Complete { get; set; } = "N";
    public string Broken { get; set; } = "N";

    /// <summary>CT-REVERSALS S9(9) COMP-5.</summary>
    public int Reversals { get; set; }

    /// <summary>CT-TOTAL-GBP S9(13)V99.</summary>
    public decimal TotalGbp { get; set; }

    /// <summary>CT-STEP OCCURS 24.</summary>
    public ChainTraceStep[] Steps { get; } = Enumerable.Range(0, MaxStepSlots).Select(_ => new ChainTraceStep()).ToArray();

    /// <summary>The STCAHDR result left by 9900-SET-RESULT.</summary>
    public LegacyResult Result { get; set; } = LegacyResult.Ok();

    /// <summary>The CHAINTRCCA body as the facade copied it (steps 1..min(count, 24)): XmdChainTraceResult.</summary>
    public JsonObject ToJson()
    {
        var steps = new JsonArray();
        var n = Math.Min(Math.Max(StepCount, 0), MaxStepSlots);
        for (var i = 0; i < n; i++)
        {
            var s = Steps[i];
            steps.Add(new JsonObject
            {
                ["seq"] = s.Seq,
                ["stage"] = s.Stage.TrimEnd(' '),
                ["docType"] = s.DocType.TrimEnd(' '),
                ["reference"] = s.Reference.TrimEnd(' '),
                ["date"] = CobolDate.FromYyyymmdd(s.Date) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                ["status"] = s.Status.TrimEnd(' '),
                ["quantity"] = CobolNumeric.WithScale(s.Quantity, 3),
                ["valueGbp"] = CobolNumeric.WithScale(s.Value, 2),
                ["kind"] = s.Kind.TrimEnd(' '),
                ["note"] = s.Note.TrimEnd(' '),
            });
        }
        return new JsonObject
        {
            ["docType"] = DocType.TrimEnd(' '),
            ["docRef"] = DocRef.TrimEnd(' '),
            ["chainType"] = ChainType.TrimEnd(' '),
            ["chainName"] = ChainName.TrimEnd(' '),
            ["party"] = Party.TrimEnd(' '),
            ["stepCount"] = StepCount,
            ["maxSteps"] = MaxSteps,
            ["truncated"] = Truncated,
            ["complete"] = Complete,
            ["broken"] = Broken,
            ["reversals"] = Reversals,
            ["totalGbp"] = CobolNumeric.WithScale(TotalGbp, 2),
            ["steps"] = steps,
        };
    }
}

/// <summary>CALL "CHAINTRC" USING CHAINTRCCA (the online enquiry and CHAINRPT's in-process call, D-XMD-02).</summary>
public interface IChainTraceService
{
    /// <summary>Runs CHAINTRC on the caller's SQLIO session; the outcome is also left on <see cref="ChainTraceArea.Result"/>.</summary>
    Task<LegacyResult> ExecuteAsync(ChainTraceArea area, CancellationToken ct = default);
}

/// <summary>
/// CHAINTRC (cobol/online/xmd/CHAINTRC.cob:60-535): business chain trace of one document. Only SELECT statements on the
/// caller's SQLIO session (autocommit): the operational steps of the chain, then every fin.Journal whose SourceRef is the
/// reference, then the completeness judgement; at most 24 steps.
/// </summary>
public sealed class ChainTraceProgram(ISqlRunner sql) : IChainTraceService
{
    private static readonly int[] Widths = Enumerable.Repeat(120, 12).ToArray();

    private sealed class Work
    {
        public int Rc;
        public string Reason = "";
        public string Msg = "";
        public int Oper;
        public int Fin;
    }

    public async Task<LegacyResult> ExecuteAsync(ChainTraceArea ca, CancellationToken ct = default)
    {
        var w = new Work();
        Initialise(ca, w);
        if (w.Rc <= 4)
        {
            if (ca.Function.Trim() == "TRACE")
            {
                await TraceAsync(ca, w, ct);
            }
            else
            {
                w.Rc = 8;
                w.Reason = "XMD0008";
                w.Msg = "Function must be TRACE.";
            }
        }
        // 9900-SET-RESULT
        ca.Result = new LegacyResult(w.Rc, CobolText.Fit(w.Reason, 8).TrimEnd(' '), CobolText.Fit(w.Msg, 200).TrimEnd(' '));
        return ca.Result;
    }

    /// <summary>0100-INITIALISE.</summary>
    private static void Initialise(ChainTraceArea ca, Work w)
    {
        ca.DocType = CobolText.Fit(ca.DocType, 12);
        ca.DocRef = CobolText.Fit(ca.DocRef, 40);
        ca.StepCount = 0;
        ca.MaxSteps = 24;
        ca.Reversals = 0;
        ca.TotalGbp = 0;
        ca.Truncated = "N";
        ca.Complete = "N";
        ca.Broken = "N";
        ca.ChainType = "";
        ca.ChainName = "";
        ca.Party = "";
        foreach (var s in ca.Steps)
        {
            s.Seq = 0;
            s.Stage = "";
            s.DocType = "";
            s.Reference = "";
            s.Date = 0;
            s.Status = "";
            s.Quantity = 0;
            s.Value = 0;
            s.Kind = "";
            s.Note = "";
        }
        if (CobolText.IsSpaces(CobolText.Fit(ca.Company, 10)))
        {
            w.Rc = 8;
            w.Reason = "XMD0001";
            w.Msg = "A company is required for a chain trace.";
            return;
        }
        if (CobolText.IsSpaces(ca.DocRef))
        {
            w.Rc = 8;
            w.Reason = "XMD0002";
            w.Msg = "A document reference is required.";
            return;
        }
        ca.DocType = CobolText.Upper(ca.DocType);
        switch (ca.DocType.TrimEnd(' '))
        {
            case "ORDER":
            case "SALESORDER":
                ca.ChainType = "O2C";
                ca.ChainName = "Order to cash";
                break;
            case "PO":
            case "PURCHASE":
                ca.ChainType = "P2P";
                ca.ChainName = "Procure to pay";
                break;
            case "KIT":
                ca.ChainType = "ASSEMBLY";
                ca.ChainName = "Make an assembly";
                break;
            default:
                w.Rc = 8;
                w.Reason = "XMD0003";
                w.Msg = "Document type must be ORDER, PO or KIT.";
                break;
        }
    }

    /// <summary>1000-TRACE: the operational steps first, then the financial effects of the same chain.</summary>
    private async Task TraceAsync(ChainTraceArea ca, Work w, CancellationToken ct)
    {
        switch (ca.ChainType)
        {
            case "O2C":
                await O2cAsync(ca, w, ct);
                break;
            case "P2P":
                await P2pAsync(ca, w, ct);
                break;
            case "ASSEMBLY":
                await KitAsync(ca, w, ct);
                break;
        }
        await FinancialAsync(ca, w, ct);
        Judge(ca, w);
    }

    /// <summary>2000-O2C.</summary>
    private async Task O2cAsync(ChainTraceArea ca, Work w, CancellationToken ct)
    {
        var row = await RunSingleAsync(ca, w,
            "SELECT 'Accepted schedules', CONVERT(varchar(24),SUM(s.Quantity)), CONVERT(varchar(24),SUM(ISNULL(s.DespatchedQty,0))), " +
            "CONVERT(varchar(12),COUNT(*)), CONVERT(varchar(12),SUM(CASE WHEN s.Status = 'CANCELLED' THEN 1 ELSE 0 END)), " +
            "ISNULL(MIN(CONVERT(char(8),s.OriginalPromiseDate,112)),'') FROM so.DeliverySchedule s WHERE s.CompanyCode = ? " +
            "AND s.OrderNumber = ?", ct);
        if (row is not null && row.Raw(3).TrimEnd(' ') != "0")
        {
            var step = AddOperational(ca, w, "Sales order schedules");
            if (step is not null)
            {
                step.DocType = "ORDER";
                step.Reference = ca.DocRef;
                step.Quantity = CobolNumeric.Store(row.NumVal(1), 11, 3, signed: true);
                step.Status = "ACCEPTED";
                var text = "Scheduled " + row.Raw(1)[..12] + " despatched " + row.Raw(2)[..12] + " cancelled lines " + row.Raw(4)[..4];
                step.Note = CobolText.Fit(text, 60);
                step.Date = IsoDate(row.Raw(5));
            }
        }
        else
        {
            ca.Broken = "Y";
            w.Rc = 16;
            w.Reason = "XMD0016";
            w.Msg = "No sales order schedule carries that number.";
            return;
        }
        ca.DocType = CobolText.Fit("DESPATCH", 12);
        await RunListAsync(ca, w, "Despatch",
            "SELECT h.DespatchRef, CONVERT(char(8),h.DespatchDate,112), h.Status, ISNULL(h.CustomerCode,''), " +
            "CONVERT(varchar(24),ISNULL(h.CogsPostedGBP,0)) FROM so.Despatch h WHERE h.CompanyCode = ? AND h.OrderNumber = ? " +
            "ORDER BY h.DespatchDate, h.DespatchRef", ct);
    }

    /// <summary>3000-P2P.</summary>
    private async Task P2pAsync(ChainTraceArea ca, Work w, CancellationToken ct)
    {
        var row = await RunSingleAsync(ca, w,
            "SELECT 'Approved schedules', CONVERT(varchar(24),SUM(s.Quantity)), CONVERT(varchar(24),SUM(ISNULL(s.ReceivedQty,0))), " +
            "CONVERT(varchar(12),COUNT(*)), '0', ISNULL(MIN(CONVERT(char(8),s.OriginalConfirmedDate,112)),'') FROM " +
            "po.PurchaseSchedule s WHERE s.CompanyCode = ? AND s.OrderNumber = ?", ct);
        if (row is not null && row.Raw(3).TrimEnd(' ') != "0")
        {
            var step = AddOperational(ca, w, "Purchase order schedules");
            if (step is not null)
            {
                step.DocType = "PO";
                step.Reference = ca.DocRef;
                step.Quantity = CobolNumeric.Store(row.NumVal(1), 11, 3, signed: true);
                step.Status = "APPROVED";
                var text = "Ordered " + row.Raw(1)[..12] + " received " + row.Raw(2)[..12] + " unreceived commitment remains open";
                step.Note = CobolText.Fit(text, 60);
                step.Date = IsoDate(row.Raw(5));
            }
        }
        else
        {
            ca.Broken = "Y";
            w.Rc = 16;
            w.Reason = "XMD0016";
            w.Msg = "No purchase schedule carries that number.";
            return;
        }
        ca.DocType = CobolText.Fit("RECEIPT", 12);
        await RunListAsync(ca, w, "Receipt and GRNI",
            "SELECT h.ReceiptRef, CONVERT(char(8),h.PhysicalDate,112), h.Status, ISNULL(h.SupplierCode,''), " +
            "CONVERT(varchar(24),ISNULL(h.GrniValueGBP,0)) FROM po.Receipt h WHERE h.CompanyCode = ? AND h.PurchaseOrderNumber = ? " +
            "ORDER BY h.PhysicalDate, h.ReceiptRef", ct);
    }

    /// <summary>4000-KIT.</summary>
    private async Task KitAsync(ChainTraceArea ca, Work w, CancellationToken ct)
    {
        var row = await RunSingleAsync(ca, w,
            "SELECT k.ParentItemCode, CONVERT(varchar(24),k.PlannedGoodQty), CONVERT(varchar(24),ISNULL(k.CompletedGoodQty,0)), " +
            "k.Status, ISNULL(k.SourceDemandRef,''), CONVERT(varchar(24),ISNULL(k.WipValueGBP,0)) FROM kit.ExecutionKit k " +
            "WHERE k.CompanyCode = ? AND k.KitRef = ?", ct);
        if (row is null)
        {
            ca.Broken = "Y";
            w.Rc = 16;
            w.Reason = "XMD0016";
            w.Msg = "No execution kit carries that reference.";
            return;
        }
        var step = AddOperational(ca, w, "Execution kit");
        if (step is not null)
        {
            step.DocType = "KIT";
            step.Reference = ca.DocRef;
            step.Quantity = CobolNumeric.Store(row.NumVal(1), 11, 3, signed: true);
            step.Value = CobolNumeric.Store(row.NumVal(5), 13, 2, signed: true);
            step.Status = CobolText.Fit(row.Raw(3), 12);
            ca.Party = CobolText.Fit(row.Raw(0), 20);
            var text = "Parent " + row.Raw(0)[..20] + " planned " + row.Raw(1)[..10] + " completed " + row.Raw(2)[..10];
            step.Note = CobolText.Fit(text, 60);
        }
        ca.DocType = CobolText.Fit("KITLINE", 12);
        await RunListAsync(ca, w, "Material issue",
            "SELECT 'Component issues', ISNULL(CONVERT(char(8),MAX(k.ModifiedAt),112),''), 'ISSUED', '', " +
            "CONVERT(varchar(24),SUM(ISNULL(l.IssuedValueGBP,0))) FROM kit.ExecutionKitLine l JOIN kit.ExecutionKit k ON " +
            "k.ExecutionKitId = l.ExecutionKitId WHERE k.CompanyCode = ? AND k.KitRef = ?", ct);
    }

    /// <summary>5000-FINANCIAL: every journal of the company whose SourceRef is the reference (no status filter).</summary>
    private async Task FinancialAsync(ChainTraceArea ca, Work w, CancellationToken ct)
    {
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await sql.FetchAllAsync(
                "SELECT j.JournalNumber, CONVERT(char(8),j.PostingDate,112), j.Status, ISNULL(j.EventCode,j.SourceModule), " +
                "CONVERT(varchar(24),ISNULL(j.TotalDebitGBP,0)), CONVERT(char(1),ISNULL(j.IsReversal,0)), ISNULL(j.SourceModule,'') " +
                "FROM fin.Journal j WHERE j.CompanyCode = ? AND ISNULL(j.SourceRef,'') = ? ORDER BY j.PostingDate, j.JournalNumber",
                Parms(ca), Widths, ct);
        }
        catch (SqlioException)
        {
            return;
        }
        foreach (var row in rows)
        {
            if (ca.StepCount < ca.MaxSteps)
            {
                ca.StepCount++;
                w.Fin++;
                var s = ca.Steps[ca.StepCount - 1];
                s.Seq = ca.StepCount % 100;
                s.DocType = "JOURNAL";
                s.Reference = CobolText.Fit(row.Raw(0), 40);
                s.Date = IsoDate(row.Raw(1));
                s.Status = CobolText.Fit(row.Raw(2), 12);
                s.Value = CobolNumeric.Store(row.NumVal(4), 13, 2, signed: true);
                ca.TotalGbp = CobolNumeric.Store(ca.TotalGbp + s.Value, 13, 2, signed: true);
                if (row.Raw(5)[0] == '1')
                {
                    s.Kind = "REVERSAL";
                    s.Stage = "Reversal or credit";
                    ca.Reversals++;
                }
                else
                {
                    s.Kind = "FINANCIAL";
                    s.Stage = "Financial effect";
                }
                var text = "Event " + row.Raw(3)[..24] + " raised by " + row.Raw(6)[..10];
                s.Note = CobolText.Fit(text, 60);
            }
            else
            {
                ca.Truncated = "Y";
            }
        }
    }

    /// <summary>6000-JUDGE: partial completion is stated, not implied.</summary>
    private static void Judge(ChainTraceArea ca, Work w)
    {
        if (ca.StepCount == 0)
        {
            ca.Broken = "Y";
            if (w.Rc < 16)
            {
                w.Rc = 16;
                w.Reason = "XMD0016";
                w.Msg = "Nothing is recorded for that reference.";
            }
            return;
        }
        if (w.Oper > 1 && w.Fin > 0) ca.Complete = "Y";
        if (w.Fin == 0)
        {
            ca.Broken = "Y";
            w.Rc = 4;
            w.Reason = "XMD0004";
            w.Msg = "The chain has no posted financial effect yet.";
        }
        if (ca.Truncated == "Y")
        {
            w.Rc = 4;
            w.Reason = "XMD0005";
            w.Msg = "More steps exist than the enquiry can return.";
        }
    }

    /// <summary>8000-RUN-SINGLE: SNGL with company and reference; an SQL error is RC 12 SQL0001 (overwritten by the not-found branch).</summary>
    private async Task<SqlioRow?> RunSingleAsync(ChainTraceArea ca, Work w, string statement, CancellationToken ct)
    {
        try
        {
            var row = await sql.SingleAsync(statement, Parms(ca), Widths, ct);
            return row.Found ? row : null;
        }
        catch (SqlioException ex)
        {
            w.Rc = 12;
            w.Reason = "SQL0001";
            w.Msg = CobolText.Fit(ex.DriverText, 200);
            return null;
        }
    }

    /// <summary>8200-ADD-OPERATIONAL: null (and truncated) when the 24 slots are used.</summary>
    private static ChainTraceStep? AddOperational(ChainTraceArea ca, Work w, string stage)
    {
        if (ca.StepCount >= ca.MaxSteps)
        {
            ca.Truncated = "Y";
            return null;
        }
        ca.StepCount++;
        w.Oper++;
        var s = ca.Steps[ca.StepCount - 1];
        s.Seq = ca.StepCount % 100;
        s.Stage = CobolText.Fit(stage, 24);
        s.Kind = "OPERATION";
        return s;
    }

    /// <summary>8300-RUN-LIST: one operational step per fetched row whose first column is not blank.</summary>
    private async Task RunListAsync(ChainTraceArea ca, Work w, string stage, string statement, CancellationToken ct)
    {
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await sql.FetchAllAsync(statement, Parms(ca), Widths, ct);
        }
        catch (SqlioException)
        {
            return;
        }
        foreach (var row in rows)
        {
            if (CobolText.IsSpaces(row.Raw(0))) continue;
            var s = AddOperational(ca, w, stage);
            if (s is null || ca.Truncated != "N") continue;
            s.DocType = CobolText.Fit(ca.DocType, 12);
            s.Reference = CobolText.Fit(row.Raw(0), 40);
            s.Date = IsoDate(row.Raw(1));
            s.Status = CobolText.Fit(row.Raw(2), 12);
            s.Value = CobolNumeric.Store(row.NumVal(4), 13, 2, signed: true);
            if (CobolText.IsSpaces(ca.Party)) ca.Party = CobolText.Fit(row.Raw(3), 20);
        }
    }

    private static SqlioParm[] Parms(ChainTraceArea ca) => [SqlioParm.Text(ca.Company), SqlioParm.Text(ca.DocRef)];

    /// <summary>8100-ISO-DATE: 8 leading digits, else yyyy-mm-dd by position, else 0.</summary>
    private static int IsoDate(string text)
    {
        var t = CobolText.Fit(text, 200);
        var head = t[..8];
        if (head.All(char.IsAsciiDigit)) return (int)CobolNumeric.Store(CobolNumeric.NumVal(head), 8, 0, signed: false);
        if (t[4] == '-')
        {
            var digits = t[..4] + t.Substring(5, 2) + t.Substring(8, 2);
            return digits.All(char.IsAsciiDigit) ? int.Parse(digits, CultureInfo.InvariantCulture) : 0;
        }
        return 0;
    }
}
