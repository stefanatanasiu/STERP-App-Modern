using System.Text.Json.Nodes;
using Sterp.Modules.Kit.Common;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Kit.Jobs;

/// <summary>
/// KITRDY (cobol/batch/kit/KITRDY.cob) - RPT-013 kit readiness (READY, any other mode), execution position (EXEC) and
/// shortage list (SHORT) on one 132-column Report Writer listing: RD PAGE LIMIT 60, HEADING 1, FIRST DETAIL 11, LAST DETAIL
/// 56; the CONTROL HEADING FINAL (scope and column headings) is never printed by the GnuCOBOL 3.2 runtime (verified on the
/// legacy executable). Reads only; return code 0, or 12 when the spool cannot be built or opened or a statement fails.
/// </summary>
internal sealed class KitrdyProgram
{
    private const string KitsSelect =
        "SELECT k.KitRef, k.ParentItemCode, k.Status," +
        " CONVERT(varchar(26),k.PlannedGoodQty)," +
        " CONVERT(varchar(26),k.CompletedGoodQty)," +
        " ISNULL(CONVERT(char(8),k.NeedDate,112),'')," +
        " CONVERT(varchar(12),COUNT(l.KitLineId))," +
        " CONVERT(varchar(12),SUM(CASE WHEN l.RemainingQty >" +
        "   ISNULL(l.ReservedQty,0) THEN 1 ELSE 0 END))," +
        " CONVERT(varchar(26),ISNULL(SUM(l.GrossIssuedQty),0))," +
        " CONVERT(varchar(26),ISNULL(SUM(l.ReturnedQty),0))," +
        " CONVERT(varchar(26),ISNULL(SUM(l.ScrappedQty),0))," +
        " CONVERT(varchar(20),k.WipValueGBP)" +
        " FROM kit.ExecutionKit k" +
        " LEFT JOIN kit.ExecutionKitLine l" +
        "   ON l.ExecutionKitId = k.ExecutionKitId" +
        "  AND l.Status <> 'CANCELLED'" +
        " WHERE k.CompanyCode = ?" +
        " AND (? = '' OR k.SiteCode = ?)" +
        " AND (? = '' OR k.Status = ?)" +
        " AND k.Status IN ('DRAFT','REVIEWED','RELEASED'," +
        "   'PARTISSUED','ISSUED','PARTCOMPLETE','OPCOMPLETE')" +
        " GROUP BY k.KitRef, k.ParentItemCode, k.Status," +
        "   k.PlannedGoodQty, k.CompletedGoodQty, k.NeedDate," +
        "   k.WipValueGBP" +
        " ORDER BY k.NeedDate, k.KitRef";

    private const string ShortagesSelect =
        "SELECT k.KitRef, l.ComponentItemCode," +
        " ISNULL(l.UomCode,'')," +
        " CONVERT(varchar(26),l.CurrentRequirementQty)," +
        " CONVERT(varchar(26),l.RemainingQty)," +
        " CONVERT(varchar(26),ISNULL(l.ReservedQty,0))," +
        " CONVERT(varchar(26),ISNULL(l.PickedQty,0))," +
        " ISNULL(CONVERT(char(8),ISNULL(l.NeedDate,k.NeedDate),112),'')," +
        " l.Status" +
        " FROM kit.ExecutionKitLine l" +
        " INNER JOIN kit.ExecutionKit k" +
        "   ON k.ExecutionKitId = l.ExecutionKitId" +
        " WHERE k.CompanyCode = ?" +
        " AND (? = '' OR k.SiteCode = ?)" +
        " AND (? = '' OR k.Status = ?)" +
        " AND k.Status IN ('RELEASED','PARTISSUED','ISSUED'," +
        "   'PARTCOMPLETE')" +
        " AND l.Status <> 'CANCELLED'" +
        " AND l.RemainingQty > ISNULL(l.ReservedQty,0)" +
        " ORDER BY ISNULL(l.NeedDate,k.NeedDate), k.KitRef," +
        "   l.LineNum";

    private const string ReadinessNote =
        "Readiness is all or nothing: a kit with one short line builds nothing, so the percentage is advisory only.";

    private readonly KitBatch _b;

    // Ws-Args
    private string _co = "";
    private string _site = "";
    private string _user = "";
    private string _mode = "";
    private string _statusArg = "";
    private int _asAt;
    private int _rc;

    // Ws-Det
    private string _dKitRef = "";
    private string _dParent = "";
    private string _dStatus = "";
    private decimal _dPlanned;
    private decimal _dCompleted;
    private string _dNeed = "";
    private int _dLines;
    private int _dShort;
    private decimal _dReadyPct;
    private string _dReadyFlag = "";
    private string _dItem = "";
    private decimal _dRequired;
    private decimal _dRemaining;
    private decimal _dReserved;
    private decimal _dPicked;
    private decimal _dIssued;
    private decimal _dReturned;
    private decimal _dScrapped;
    private decimal _dWip;
    private string _dUom = "";

    // Ws-Tot
    private int _tKits;
    private int _tReady;
    private int _tShortLines;
    private decimal _tWip;

    private readonly JsonArray _kits = [];
    private readonly JsonArray _shortages = [];

    private KitrdyProgram(JobRunContext ctx) => _b = new KitBatch(ctx, "KITRDY");

    public static Task<int> RunAsync(JobRunContext ctx) => new KitrdyProgram(ctx).MainAsync();

    private async Task<int> MainAsync()
    {
        ReadArgs();
        await _b.JobStartAsync(_co, _user);
        GnuReportWriter? report = null;
        if (await OpenSpoolAsync())
        {
            report = new GnuReportWriter(_b.Spool!.WriteLine, _b.Heading, 60, 1, 11, 56, null, FinalFooting());
            report.Initiate();
            if (_mode == "SHORT") await PrintShortagesAsync(report);
            else await PrintKitsAsync(report);
            report.Terminate();
            _b.CloseSpool();
            _b.Display("STERP-I-KIT1001 listing written to " + Pic.Trim(_b.SpoolName));
        }
        await _b.JobEndAsync(_co, _user, _tKits, _rc);
        _b.Ctx.Result = Result();
        return _rc;
    }

    /// <summary>0100-READ-ARGS: CMDARGS named fields and STATUS; CO STC, USER STORES, MODE READY and ASAT today by default.</summary>
    private void ReadArgs()
    {
        var args = _b.Ctx.Args;
        _co = Pic.X(args.Co, 10);
        _site = Pic.X(args.Site, 10);
        _user = Pic.X(args.User, 20);
        _mode = Pic.X(args.Mode, 10);
        _statusArg = Pic.X(args.Get("STATUS"), 14);
        if (!Pic.Blank(args.AsAt)) _asAt = KitBatch.Date8(args.AsAt);
        if (Pic.Blank(_co)) _co = "STC";
        if (Pic.Blank(_user)) _user = "STORES";
        if (Pic.Blank(_mode)) _mode = "READY";
        if (_asAt == 0) _asAt = KitBatch.Today();
        _b.Display("STERP-I-KIT1000 KITRDY " + Pic.Trim(_mode) + " listing for " + Pic.Trim(_co));
    }

    /// <summary>0300-OPEN-SPOOL with 0350-FILL-HEADINGS; RC 12 when the path cannot be built or the file opened.</summary>
    private async Task<bool> OpenSpoolAsync()
    {
        if (!_b.BuildSpoolPath(_co))
        {
            _rc = 12;
            _b.Display("STERP-E-KIT1002 cannot build the spool path");
            return false;
        }
        await FillHeadingsAsync();
        if (!_b.OpenSpool())
        {
            _rc = 12;
            _b.Display("STERP-E-KIT1003 cannot open " + Pic.Pad(_b.SpoolName, 200));
            return false;
        }
        return true;
    }

    private async Task FillHeadingsAsync()
    {
        var h = _b.Heading;
        h.CompanyCode = _co;
        h.ReportId = "KITRDY";
        h.Title = "Kit readiness and execution";
        h.User = _user;
        h.RunAt = DateTime.Now;
        h.AsAtText = Pic.Str(30, "As at ", KitBatch.Dmy(_asAt));
        h.Subtitle = _mode switch
        {
            "EXEC" => "Execution position (RPT-013)",
            "SHORT" => "Shortage list (RPT-013)",
            _ => "Readiness to pick (RPT-013)",
        };
        await _b.ReadCompanyAsync(_co);
    }

    private IReadOnlyList<SqlioParm> ScopeParms() =>
    [
        SqlioParm.Text(_co), SqlioParm.Text(_site), SqlioParm.Text(_site), SqlioParm.Text(_statusArg), SqlioParm.Text(_statusArg),
    ];

    // ------------------------------------------------------------------ READY / EXEC

    /// <summary>1000-PRINT-KITS: the open kits (seven statuses) by need date and reference with their line counts.</summary>
    private async Task PrintKitsAsync(GnuReportWriter report)
    {
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await _b.FetchAsync(KitsSelect, ScopeParms());
        }
        catch (SqlioException ex)
        {
            _rc = 12;
            _b.Display("STERP-E-KIT1004 " + KitBatch.ErrText(ex));
            return;
        }
        var detail = _mode == "EXEC" ? ExecLine() : KitLine();
        foreach (var row in rows) PrintOneKit(report, row, detail);
    }

    /// <summary>1100-PRINT-ONE-KIT: readiness % ROUNDED to one decimal, Ready only when no line is short.</summary>
    private void PrintOneKit(GnuReportWriter report, SqlioRow row, RwLine detail)
    {
        _dKitRef = Pic.X(row.Raw(0), 16);
        _dParent = Pic.X(row.Raw(1), 22);
        _dStatus = Pic.X(row.Raw(2), 13);
        _dPlanned = Pic.N(row.NumVal(3), 11, 3);
        _dCompleted = Pic.N(row.NumVal(4), 11, 3);
        _dNeed = KitBatch.DateDigits(row.Raw(5)) is { } need ? KitBatch.Dmy(need) : "";
        _dLines = (int)Pic.N(row.NumVal(6), 5, 0);
        _dShort = (int)Pic.N(row.NumVal(7), 5, 0);
        _dIssued = Pic.N(row.NumVal(8), 11, 3);
        _dReturned = Pic.N(row.NumVal(9), 11, 3);
        _dScrapped = Pic.N(row.NumVal(10), 11, 3);
        _dWip = Pic.N(row.NumVal(11), 11, 2);
        _dReadyPct = _dLines > 0 ? Pic.R((_dLines - _dShort) * 100m / _dLines, 3, 1) : 0m;
        if (_dShort == 0 && _dLines > 0)
        {
            _dReadyFlag = "YES";
            _tReady = (int)Pic.N(_tReady + 1, 7, 0);
        }
        else
        {
            _dReadyFlag = "NO";
        }
        _tKits = (int)Pic.N(_tKits + 1, 7, 0);
        _tShortLines = (int)Pic.N(_tShortLines + _dShort, 7, 0);
        _tWip = Pic.N(_tWip + _dWip, 13, 2);
        _kits.Add(new JsonObject
        {
            ["kitRef"] = Pic.X(row.Raw(0), 30),
            ["parentItemCode"] = Pic.X(row.Raw(1), 40),
            ["status"] = Pic.X(row.Raw(2), 14),
            ["plannedGoodQty"] = Pic.S(row.NumVal(3), 6),
            ["completedGoodQty"] = Pic.S(row.NumVal(4), 6),
            ["needDate"] = KitBatch.JsonDate(row.Raw(5)),
            ["lines"] = _dLines,
            ["shortLines"] = _dShort,
            ["readyPct"] = Pic.S(_dReadyPct, 1),
            ["ready"] = _dReadyFlag,
            ["issuedQty"] = Pic.S(row.NumVal(8), 6),
            ["returnedQty"] = Pic.S(row.NumVal(9), 6),
            ["scrappedQty"] = Pic.S(row.NumVal(10), 6),
            ["wipValueGBP"] = Pic.S(row.NumVal(11), 2),
        });
        report.Generate(detail);
    }

    /// <summary>DT-KIT.</summary>
    private RwLine KitLine() => RwLine.Plus1(() => new RwText()
        .Put(1, _dKitRef, 16).Put(18, _dParent, 22).Put(41, _dStatus, 13)
        .Num(55, _dPlanned, "Z(9)9.999").Num(70, _dCompleted, "Z(9)9.999").Put(85, _dNeed, 10)
        .Num(96, _dLines, "Z(4)9").Num(103, _dShort, "Z(4)9").Num(110, _dReadyPct, "Z(2)9.9").Put(120, _dReadyFlag, 3).Text);

    /// <summary>DT-EXEC.</summary>
    private RwLine ExecLine() => RwLine.Plus1(() => new RwText()
        .Put(1, _dKitRef, 16).Put(18, _dParent, 22).Put(41, _dStatus, 13)
        .Num(55, _dIssued, "Z(9)9.999").Num(70, _dReturned, "Z(9)9.999").Num(85, _dScrapped, "Z(9)9.999")
        .Num(100, _dCompleted, "Z(9)9.999").Num(115, _dWip, "Z(9)9.99").Text);

    // ------------------------------------------------------------------ SHORT

    /// <summary>2000-PRINT-SHORTAGES: one line per material line still short of its reservation.</summary>
    private async Task PrintShortagesAsync(GnuReportWriter report)
    {
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await _b.FetchAsync(ShortagesSelect, ScopeParms());
        }
        catch (SqlioException ex)
        {
            _rc = 12;
            _b.Display("STERP-E-KIT1005 " + KitBatch.ErrText(ex));
            return;
        }
        var detail = ShortLine();
        foreach (var row in rows)
        {
            // 2100-PRINT-ONE-SHORTAGE
            _dKitRef = Pic.X(row.Raw(0), 16);
            _dItem = Pic.X(row.Raw(1), 26);
            _dUom = Pic.X(row.Raw(2), 6);
            _dRequired = Pic.N(row.NumVal(3), 11, 3);
            _dRemaining = Pic.N(row.NumVal(4), 11, 3);
            _dReserved = Pic.N(row.NumVal(5), 11, 3);
            _dPicked = Pic.N(row.NumVal(6), 11, 3);
            _dNeed = KitBatch.DateDigits(row.Raw(7)) is { } need ? KitBatch.Dmy(need) : "";
            _dStatus = Pic.X(row.Raw(8), 13);
            _tShortLines = (int)Pic.N(_tShortLines + 1, 7, 0);
            _shortages.Add(new JsonObject
            {
                ["kitRef"] = Pic.X(row.Raw(0), 30),
                ["componentItemCode"] = Pic.X(row.Raw(1), 40),
                ["uomCode"] = Pic.X(row.Raw(2), 10),
                ["currentRequirementQty"] = Pic.S(row.NumVal(3), 6),
                ["remainingQty"] = Pic.S(row.NumVal(4), 6),
                ["reservedQty"] = Pic.S(row.NumVal(5), 6),
                ["pickedQty"] = Pic.S(row.NumVal(6), 6),
                ["needDate"] = KitBatch.JsonDate(row.Raw(7)),
                ["lineStatus"] = Pic.X(row.Raw(8), 12),
            });
            report.Generate(detail);
        }
    }

    /// <summary>DT-SHORT.</summary>
    private RwLine ShortLine() => RwLine.Plus1(() => new RwText()
        .Put(1, _dKitRef, 16).Put(18, _dItem, 26).Put(45, _dUom, 6)
        .Num(52, _dRequired, "Z(9)9.999").Num(67, _dRemaining, "Z(9)9.999").Num(82, _dReserved, "Z(9)9.999")
        .Num(97, _dPicked, "Z(9)9.999").Put(112, _dNeed, 10).Put(124, _dStatus, 6).Text);

    /// <summary>CF-FINAL.</summary>
    private RwLine[] FinalFooting() =>
    [
        RwLine.PlusN(2, () => new string('=', 130)),
        RwLine.Plus1(() => new RwText()
            .Put(1, "Kits listed         ", 20).Num(22, _tKits, "Z(6)9")
            .Put(34, "Ready to pick       ", 20).Num(55, _tReady, "Z(6)9")
            .Put(68, "Short lines         ", 20).Num(89, _tShortLines, "Z(6)9").Text),
        RwLine.Plus1(() => new RwText().Put(1, "Work in progress value GBP  ", 28).Num(30, _tWip, "Z(11)9.99").Text),
        RwLine.PlusN(2, () => new RwText().Put(1, ReadinessNote, 120).Text),
    ];

    /// <summary>KitRdyReportResult (GET /api/jobs/v1/runs/{id}/result).</summary>
    private JsonObject Result() => new()
    {
        ["mode"] = Pic.X(_mode, 10),
        ["company"] = Pic.X(_co, 10),
        ["asAtText"] = Pic.D8(_asAt),
        ["kits"] = _kits,
        ["shortages"] = _shortages,
        ["footing"] = new JsonObject
        {
            ["kitsListed"] = _tKits,
            ["readyToPick"] = _tReady,
            ["shortLines"] = _tShortLines,
            ["wipValueGBP"] = Pic.S(_tWip, 2),
        },
    };
}
