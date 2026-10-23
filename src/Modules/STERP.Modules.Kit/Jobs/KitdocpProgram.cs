using System.Text.Json.Nodes;
using Sterp.Modules.Kit.Common;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Kit.Jobs;

/// <summary>
/// KITDOCP (cobol/batch/kit/KITDOCP.cob) - RPT-014 picking document of one kit, original or reprint: RD PAGE LIMIT 60,
/// HEADING 1, FIRST DETAIL 6, LAST DETAIL 52; the kit header is a DETAIL group the program generates itself, every
/// non-cancelled line a two-line DETAIL, the CONTROL FOOTING FINAL the totals and signature lines. Reads only (the paper
/// moves no stock). Return code 0 (also with no printable lines), 8 kit not found, 12 KIT missing, spool or SQL failure.
/// </summary>
internal sealed class KitdocpProgram
{
    private const string HeaderSelect =
        "SELECT k.ParentItemCode, ISNULL(i.Description,'')," +
        " k.Status, k.SiteCode," +
        " CONVERT(varchar(26),k.PlannedGoodQty)," +
        " CONVERT(varchar(26),k.CompletedGoodQty)," +
        " ISNULL(CONVERT(char(8),k.NeedDate,112),'')," +
        " ISNULL(k.SourceDemandType,'') + ' '" +
        "   + ISNULL(k.SourceDemandRef,'')," +
        " ISNULL(k.OwnerUser,'')," +
        " CASE WHEN k.IsBackflush = 1 THEN 'Yes' ELSE 'No' END" +
        " FROM kit.ExecutionKit k" +
        " LEFT JOIN inv.Item i ON i.CompanyCode = k.CompanyCode" +
        "   AND i.ItemCode = k.ParentItemCode" +
        " WHERE k.CompanyCode = ? AND k.KitRef = ?" +
        " AND (? = '' OR k.SiteCode = ?)";

    private const string LinesSelect =
        "SELECT CONVERT(varchar(12),l.LineNum)," +
        " l.ComponentItemCode, ISNULL(i.Description,'')," +
        " l.UomCode," +
        " CONVERT(varchar(26),l.CurrentRequirementQty)," +
        " CONVERT(varchar(26),l.RemainingQty)," +
        " CONVERT(varchar(26),l.ReservedQty)," +
        " CONVERT(varchar(26),l.PickedQty)," +
        " ISNULL(l.IssueLocationCode,'')," +
        " ISNULL(i.TraceabilityType,'NONE'), l.Status" +
        " FROM kit.ExecutionKitLine l" +
        " JOIN kit.ExecutionKit k" +
        "   ON k.ExecutionKitId = l.ExecutionKitId" +
        " LEFT JOIN inv.Item i ON i.CompanyCode = k.CompanyCode" +
        "   AND i.ItemCode = l.ComponentItemCode" +
        " WHERE k.CompanyCode = ? AND k.KitRef = ?" +
        "   AND l.Status <> 'CANCELLED'" +
        " ORDER BY l.LineNum";

    private const string StockNote =
        "This document moves no stock.  Material is only issued when KITISSUE posts it, and only against the reservation.";

    private readonly KitBatch _b;

    // Ws-Args
    private string _co = "";
    private string _site = "";
    private string _user = "";
    private string _mode = "";
    private string _kitRef = "";
    private int _copyNo;
    private string _reason = "";
    private int _rc;
    private string _found = "N";

    // Ws-Hdr
    private string _hKitRef = "";
    private string _hParent = "";
    private string _hParentDesc = "";
    private string _hStatus = "";
    private string _hSite = "";
    private decimal _hPlanned;
    private decimal _hCompleted;
    private string _hNeed = "";
    private string _hSource = "";
    private string _hOwner = "";
    private string _hBackflush = "";
    private string _hDocKind = "";
    private string _hCopyText = "";

    // Ws-Det
    private int _dLine;
    private string _dItem = "";
    private string _dDesc = "";
    private string _dUom = "";
    private decimal _dRequired;
    private decimal _dRemaining;
    private decimal _dReserved;
    private decimal _dPicked;
    private string _dLocation = "";
    private string _dTrace = "";
    private string _dStatus = "";

    // Ws-Tot
    private int _tLines;
    private int _tTraced;
    private decimal _tOutstanding;

    private JsonObject _header = new();
    private readonly JsonArray _lines = [];

    private KitdocpProgram(JobRunContext ctx) => _b = new KitBatch(ctx, "KITDOCP");

    public static Task<int> RunAsync(JobRunContext ctx) => new KitdocpProgram(ctx).MainAsync();

    private async Task<int> MainAsync()
    {
        ReadArgs();
        await _b.JobStartAsync(_co, _user);
        if (_rc <= 4 && await OpenSpoolAsync())
        {
            var report = new GnuReportWriter(_b.Spool!.WriteLine, _b.Heading, 60, 1, 6, 52, null, FinalFooting());
            report.Initiate();
            await PrintHeaderAsync();
            if (_found == "Y")
            {
                report.Generate(HeadLines());
                await PrintLinesAsync(report);
            }
            report.Terminate();
            _b.CloseSpool();
            _b.Display("STERP-I-KIT1018 " + Pic.Trim(_hDocKind) + " written to " + Pic.Trim(_b.SpoolName));
        }
        await _b.JobEndAsync(_co, _user, _tLines, _rc);
        _b.Ctx.Result = Result();
        return _rc;
    }

    /// <summary>0100-READ-ARGS with 0180-DESCRIBE-DOCUMENT: CO STC, USER STORES, MODE ORIGINAL; a blank KIT is RC 12 KIT1010.</summary>
    private void ReadArgs()
    {
        var args = _b.Ctx.Args;
        _co = Pic.X(args.Co, 10);
        _site = Pic.X(args.Site, 10);
        _user = Pic.X(args.User, 20);
        _mode = Pic.X(args.Mode, 10);
        _kitRef = Pic.X(args.Get("KIT"), 30);
        var copy = args.Get("COPY");
        if (!Pic.Blank(copy)) _copyNo = (int)Pic.N(CobolNumeric.NumVal(copy), 4, 0);
        _reason = Pic.X(args.Get("REASON"), 60);
        if (Pic.Blank(_co)) _co = "STC";
        if (Pic.Blank(_user)) _user = "STORES";
        if (Pic.Blank(_mode)) _mode = "ORIGINAL";
        DescribeDocument();
        if (Pic.Blank(_kitRef))
        {
            _rc = 12;
            _b.Display("STERP-E-KIT1010 KIT= is required - a kit document is printed for one kit");
        }
        else
        {
            _b.Display("STERP-I-KIT1011 KITDOCP " + Pic.Trim(_hDocKind) + " for " + Pic.Trim(_kitRef));
        }
    }

    private bool IsReprint => _mode is "REPRINT" or "COPY";

    /// <summary>0180-DESCRIBE-DOCUMENT: a reprint is never allowed to look like an original.</summary>
    private void DescribeDocument()
    {
        _hCopyText = "";
        if (IsReprint)
        {
            _hDocKind = "REPRINT - PICKING LIST COPY";
            if (_copyNo < 2) _copyNo = 2;
            if (Pic.Blank(_reason)) _reason = "not stated";
            _hCopyText = Pic.Str(60, "*** REPRINT copy ", Pic.Digits(_copyNo, 4), " - reason: ", Pic.Trim(_reason), " ***");
        }
        else
        {
            _hDocKind = "ORIGINAL PICKING LIST";
            _copyNo = 1;
            _hCopyText = "Original issue of this document.";
        }
    }

    /// <summary>0300-OPEN-SPOOL with 0350-FILL-HEADINGS (RPT-RUN-SS is never set: the run time shows seconds 00).</summary>
    private async Task<bool> OpenSpoolAsync()
    {
        if (!_b.BuildSpoolPath(_co))
        {
            _rc = 12;
            _b.Display("STERP-E-KIT1012 cannot build the spool path");
            return false;
        }
        var h = _b.Heading;
        h.CompanyCode = _co;
        h.ReportId = "KITDOCP";
        h.Title = IsReprint ? "Kit picking document - REPRINT" : "Kit picking document";
        h.User = _user;
        var now = DateTime.Now;
        h.RunAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Local);
        await _b.ReadCompanyAsync(_co);
        if (!_b.OpenSpool())
        {
            _rc = 12;
            _b.Display("STERP-E-KIT1013 cannot open " + Pic.Pad(_b.SpoolName, 200));
            return false;
        }
        return true;
    }

    /// <summary>1000-PRINT-HEADER: one kit by company and reference (and site when given); not found RC 8 KIT1014.</summary>
    private async Task PrintHeaderAsync()
    {
        _found = "N";
        SqlioRow row;
        try
        {
            row = await _b.SingleAsync(HeaderSelect,
                [SqlioParm.Text(_co), SqlioParm.Text(_kitRef), SqlioParm.Text(_site), SqlioParm.Text(_site)]);
        }
        catch (SqlioException ex)
        {
            _rc = 12;
            _b.Display("STERP-E-KIT1015 " + KitBatch.ErrText(ex));
            return;
        }
        if (!row.Found)
        {
            _rc = 8;
            _b.Display("STERP-W-KIT1014 kit " + Pic.Trim(_kitRef) + " is not known in " + Pic.Trim(_co));
            return;
        }
        _found = "Y";
        _hKitRef = _kitRef;
        _hParent = Pic.X(row.Raw(0), 40);
        _hParentDesc = Pic.X(row.Raw(1), 40);
        _hStatus = Pic.X(row.Raw(2), 14);
        _hSite = Pic.X(row.Raw(3), 10);
        _hPlanned = Pic.N(row.NumVal(4), 11, 3);
        _hCompleted = Pic.N(row.NumVal(5), 11, 3);
        _hNeed = KitBatch.DateDigits(row.Raw(6)) is { } need ? KitBatch.Dmy(need) : "";
        _hSource = Pic.X(row.Raw(7), 40);
        _hOwner = Pic.X(row.Raw(8), 20);
        _hBackflush = Pic.X(row.Raw(9), 3);
        _header = new JsonObject
        {
            ["kitRef"] = _kitRef,
            ["status"] = Pic.X(row.Raw(2), 14),
            ["parentItemCode"] = Pic.X(row.Raw(0), 40),
            ["parentDescription"] = Pic.X(row.Raw(1), 100),
            ["plannedGoodQty"] = Pic.S(row.NumVal(4), 6),
            ["completedGoodQty"] = Pic.S(row.NumVal(5), 6),
            ["needDate"] = KitBatch.JsonDate(row.Raw(6)),
            ["siteCode"] = Pic.X(row.Raw(3), 10),
            ["raisedFor"] = Pic.X(row.Raw(7), 53),
            ["backflush"] = Pic.X(row.Raw(9), 3),
            ["ownerUser"] = Pic.X(row.Raw(8), 20),
        };
    }

    /// <summary>2000-PRINT-LINES: the material lines that are not cancelled, by line number; none is KIT1017 (RC unchanged).</summary>
    private async Task PrintLinesAsync(GnuReportWriter report)
    {
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await _b.FetchAsync(LinesSelect, [SqlioParm.Text(_co), SqlioParm.Text(_kitRef)]);
        }
        catch (SqlioException ex)
        {
            _rc = 12;
            _b.Display("STERP-E-KIT1016 " + KitBatch.ErrText(ex));
            return;
        }
        var detail = DetailLines();
        foreach (var row in rows)
        {
            PrintOneLine(row);
            report.Generate(detail);
        }
        if (_tLines == 0)
            _b.Display("STERP-W-KIT1017 kit " + Pic.Trim(_kitRef) + " has no material lines to pick");
    }

    /// <summary>2100-PRINT-ONE-LINE: the identify column says whether the component must be picked by lot or serial (D-20).</summary>
    private void PrintOneLine(SqlioRow row)
    {
        _dLine = (int)Pic.N(row.NumVal(0), 5, 0);
        _dItem = Pic.X(row.Raw(1), 26);
        _dDesc = Pic.X(row.Raw(2), 28);
        _dUom = Pic.X(row.Raw(3), 6);
        _dRequired = Pic.N(row.NumVal(4), 11, 3);
        _dRemaining = Pic.N(row.NumVal(5), 11, 3);
        _dReserved = Pic.N(row.NumVal(6), 11, 3);
        _dPicked = Pic.N(row.NumVal(7), 11, 3);
        _dLocation = Pic.X(row.Raw(8), 14);
        if (Pic.Blank(_dLocation)) _dLocation = "any";
        _dTrace = Pic.Pad(row.Raw(9), 6)[..6] switch
        {
            "LOT   " => "Lot",
            "SERIAL" => "Serial",
            "BOTH  " => "Lot+Ser",
            _ => "-",
        };
        if (_dTrace != "-") _tTraced = (int)Pic.N(_tTraced + 1, 7, 0);
        _dStatus = Pic.X(row.Raw(10), 11);
        _tLines = (int)Pic.N(_tLines + 1, 7, 0);
        _tOutstanding = Pic.N(_tOutstanding + _dRemaining, 13, 3);
        var location = Pic.X(row.Raw(8), 20);
        _lines.Add(new JsonObject
        {
            ["lineNum"] = _dLine,
            ["componentItemCode"] = Pic.X(row.Raw(1), 40),
            ["description"] = Pic.X(row.Raw(2), 100),
            ["uomCode"] = Pic.X(row.Raw(3), 10),
            ["currentRequirementQty"] = Pic.S(row.NumVal(4), 6),
            ["outstandingQty"] = Pic.S(row.NumVal(5), 6),
            ["reservedQty"] = Pic.S(row.NumVal(6), 6),
            ["identification"] = _dTrace,
            ["lineStatus"] = Pic.X(row.Raw(10), 12),
            ["pickFrom"] = Pic.Blank(location) ? "any" : location,
            ["pickedQty"] = Pic.S(row.NumVal(7), 6),
        });
    }

    /// <summary>DT-HEAD: the kit header block and the column headings (a DETAIL, since CONTROL HEADING FINAL is never printed).</summary>
    private RwLine[] HeadLines() =>
    [
        RwLine.Plus1(() => new RwText()
            .Put(1, "Document      ", 14).Put(16, _hDocKind, 28).Put(48, "Kit reference ", 14).Put(63, _hKitRef, 30)
            .Put(96, "Status  ", 8).Put(105, _hStatus, 14).Text),
        RwLine.Plus1(() => new RwText()
            .Put(1, "Parent item   ", 14).Put(16, _hParent, 28).Put(48, "Description   ", 14).Put(63, _hParentDesc, 40).Text),
        RwLine.Plus1(() => new RwText()
            .Put(1, "Planned qty   ", 14).Num(16, _hPlanned, "Z(9)9.999").Put(48, "Completed qty ", 14).Num(63, _hCompleted, "Z(9)9.999")
            .Put(96, "Need by ", 8).Put(105, _hNeed, 10).Text),
        RwLine.Plus1(() => new RwText()
            .Put(1, "Site          ", 14).Put(16, _hSite, 10).Put(48, "Raised for    ", 14).Put(63, _hSource, 40)
            .Put(105, "Backflush ", 10).Put(116, _hBackflush, 3).Text),
        RwLine.Plus1(() => new RwText().Put(1, "Kit owner     ", 14).Put(16, _hOwner, 20).Put(48, _hCopyText, 60).Text),
        RwLine.Plus1(() => new string('=', 130)),
        RwLine.Plus1(() => new RwText()
            .Put(1, " Line", 5).Put(8, "Component item            ", 26).Put(35, "Description                 ", 28)
            .Put(64, "UOM   ", 6).Put(71, "      Required", 14).Put(86, "   Outstanding", 14).Put(101, "  Reserved", 10)
            .Put(112, "Identify", 8).Put(121, "Line state", 10).Text),
        RwLine.Plus1(() => new string('-', 130)),
    ];

    /// <summary>DT-LINE: the material line and its pick-from / signature line.</summary>
    private RwLine[] DetailLines() =>
    [
        RwLine.Plus1(() => new RwText()
            .Num(1, _dLine, "Z(4)9").Put(8, _dItem, 26).Put(35, _dDesc, 28).Put(64, _dUom, 6)
            .Num(71, _dRequired, "Z(9)9.999").Num(86, _dRemaining, "Z(9)9.999").Num(101, _dReserved, "Z(5)9.999")
            .Put(112, _dTrace, 8).Put(121, _dStatus, 11).Text),
        RwLine.Plus1(() => new RwText()
            .Put(8, "Pick from     ", 14).Put(23, _dLocation, 14).Put(40, "Already picked", 14).Num(55, _dPicked, "Z(5)9.999")
            .Put(71, "Picked ......... Checked by ................", 44).Text),
    ];

    /// <summary>CF-FINAL: totals, the no-stock statement, the copy line and the signature line.</summary>
    private RwLine[] FinalFooting() =>
    [
        RwLine.Plus1(() => new string('-', 130)),
        RwLine.Plus1(() => new RwText()
            .Put(1, "Material lines printed  ", 24).Num(26, _tLines, "Z(6)9")
            .Put(40, "Lines needing lot or serial   ", 30).Num(71, _tTraced, "Z(6)9")
            .Put(86, "Outstanding qty   ", 18).Num(105, _tOutstanding, "Z(11)9.999").Text),
        RwLine.PlusN(2, () => new RwText().Put(1, StockNote, 118).Text),
        RwLine.Plus1(() => new RwText().Put(1, _hCopyText, 60).Text),
        RwLine.PlusN(2, () => new RwText()
            .Lit(1, "Issued by ...............................")
            .Lit(48, "Received by .............................")
            .Put(96, "Date ....../....../........", 28).Text),
    ];

    /// <summary>KitDocpReportResult (GET /api/jobs/v1/runs/{id}/result): the values before print editing.</summary>
    private JsonObject Result()
    {
        // a kit that was not read leaves the header fields at their initial values: only the reference as entered is known
        if (_found != "Y") _header = new JsonObject { ["kitRef"] = _kitRef };
        return new JsonObject
        {
            ["documentKind"] = Pic.X(_hDocKind, 30),
            ["copyNo"] = _copyNo,
            ["copyText"] = Pic.X(_hCopyText, 80),
            ["found"] = _found,
            ["header"] = _header,
            ["lines"] = _lines,
            ["footing"] = new JsonObject
            {
                ["linesPrinted"] = _tLines,
                ["linesNeedingIdentification"] = _tTraced,
                ["totalOutstanding"] = Pic.S(_tOutstanding, 6),
            },
        };
    }
}
