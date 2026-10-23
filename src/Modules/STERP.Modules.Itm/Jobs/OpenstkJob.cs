using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Data;
using Sterp.Modules.Itm.Services;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Itm.Jobs;

/// <summary>
/// OPENSTK (cobol/batch/itm/OPENSTK.cob; FR-STERP-ITM-036, file FL-043 FR-STERP-ITM-037): opening stock load - proves every
/// row of the CSV and posts the accepted rows through STKMOVE OPENING with a per-line correlation id; the spool lists the
/// exceptions. No batch unit of work: each row commits in its own STKMOVE transaction (FR-OPENSTK-022).
/// </summary>
internal static class OpenstkJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "OPENSTK",
        Slice = "STERP-ITM",
        SubmitOperationId = "itm_submitOpenstkRun",
        ParameterOrder = ["CO", "SITE", "USER", "MODE", "ASAT", "IN"],
        PreCheck = pre =>
        {
            var a = Arguments.Read(pre.Args, DateTime.Now);
            // 0100-READ-ARGS refuses before the program's JOBLOG START
            return Task.FromResult(a.Mode is not ("PREVIEW" or "COMMIT")
                ? LegacyResult.Error("E-0101", "STERP-E-0101 MODE must be PREVIEW or COMMIT")
                : (LegacyResult?)null);
        },
        SelfRegistration = pre =>
        {
            var a = Arguments.Read(pre.Args, DateTime.Now);
            return ItmJobSubmission.JoblogStart("OPENSTK", a.Co, a.User, "", pre.Args.Text);
        },
        EndValues = ItmJobSubmission.NeverEnded,
        Execute = RunAsync,
    };

    private sealed record Arguments(string Co, string Site, string User, int AsAt, string Mode, string In)
    {
        public static Arguments Read(Sterp.Platform.Kernel.Jobs.CommandLineArgs a, DateTime now)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var mode = Cob.X(a.Mode, 10);
            var input = a.Get("IN") is { } i ? Cob.X(i, 200) : "";
            var asat = Cob.Spaces(a.AsAt) ? 0 : Batch.Date8(a.AsAt);
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "STKACCT";
            if (Cob.Spaces(mode)) mode = "PREVIEW";
            if (asat == 0) asat = CobolDate.ToYyyymmdd(now);
            return new Arguments(co, Cob.X(a.Site, 10), user, asat, mode, input);
        }
    }

    private static string Ddmmyyyy(int yyyymmdd)
    {
        var d = Cob.D8(yyyymmdd);
        return d[6..8] + "/" + d[4..6] + "/" + d[..4];
    }

    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var a = Arguments.Read(ctx.Args, DateTime.Now);
        var input = a.In;
        if (Cob.Spaces(input))
        {
            // RUNPATH BUILD EXTRACTS/OPENSTK-<CO>.csv without a stamp (a failed build leaves the name blank)
            var p = ctx.Paths.Build("EXTRACTS", "OPENSTK", a.Co, "csv", null, false, out var built);
            input = p.IsOk ? built : "";
        }
        ctx.Display("STERP-I-0100 OPENSTK " + Cob.Trim(a.Mode) + " load from " + Cob.Trim(input));
        var sql = ctx.Get<Sqlio>();

        // 0300-OPEN-SPOOL: RUNPATH, headings, OPEN INPUT LOAD-FILE, OPEN OUTPUT RPT-FILE
        var path = ctx.Paths.Build("SPOOL", "OPENSTK", a.Co, null, null, true, out var spoolName);
        if (path.ReturnCode > 4)
        {
            ctx.Display("STERP-E-0301 cannot build the spool path");
            return 12;
        }
        var heading = await Batch.HeadingAsync(sql, a.Co, "OPENSTK", "Opening stock load - exceptions", "Mode " + Cob.Trim(a.Mode),
            "Opening at " + Ddmmyyyy(a.AsAt), a.User, ct);
        var records = ReadLoadFile(ctx, input);
        if (records is null)
        {
            ctx.Display("STERP-E-0303 cannot read " + Cob.Trim(input));
            return 12;
        }
        SpoolWriter spool;
        try
        {
            spool = new SpoolWriter(spoolName);
            ctx.SpoolFiles.Add(spoolName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Display("STERP-E-0302 cannot open " + Batch.Field(spoolName, 200));
            return 12;
        }

        var rc = 0;
        var load = new Load(ctx, a);
        using (spool)
        {
            load.Open(spool, heading);
            // 1000-LOAD-FILE
            foreach (var rec in records) await load.OneRowAsync(rec, ct);
            load.Terminate();
        }

        // 0400-CLOSE-SPOOL
        if (load.Rejected > 0)
        {
            if (rc < 4) rc = 4;
            ctx.Display("STERP-W-0402 " + Batch.Disp(load.Rejected, 7) + " row(s) rejected");
        }
        ctx.Display("STERP-I-0403 " + Batch.Disp(load.Read, 7) + " row(s) read, " + Batch.Disp(load.Posted, 7) + " posted");
        ctx.AnnounceSpool(spoolName.Trim(' '));
        ctx.RowsProcessed = load.Posted;
        return rc;
    }

    /// <summary>
    /// LOAD-FILE (LINE SEQUENTIAL, LOAD-REC PIC X(300)): every line is a record cut or padded to 300 bytes (a carriage return
    /// before the line feed is not data); null when OPEN INPUT fails. A relative name is taken under the run root.
    /// </summary>
    private static List<string>? ReadLoadFile(JobRunContext ctx, string input)
    {
        var name = input.Trim(' ');
        if (name.Length == 0) return null;
        try
        {
            var full = Path.IsPathRooted(name) ? name : Path.Combine(ctx.Paths.Base, name);
            byte[] bytes;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            var text = CobolText.Ansi.GetString(bytes);
            var records = new List<string>();
            var start = 0;
            for (var i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n') continue;
                if (i == text.Length && start >= text.Length) break;
                var line = text[start..i];
                if (line.EndsWith('\r')) line = line[..^1];
                records.Add(CobolText.Fit(line, 300));
                start = i + 1;
            }
            return records;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The program's row processing and totals.</summary>
    private sealed class Load(JobRunContext ctx, Arguments a)
    {
        private GnuReportWriter? _rw;
        private RwLine[] _detail = [];
        private int _xLine;
        private string _xItem = "";
        private string _xLoc = "";
        private string _xLot = "";
        private decimal _xQty;
        private string _xReason = "";
        private string _xWhy = "";
        public int Read;
        public int Posted;
        public int Rejected;
        private int _duplicate;
        private decimal _qty;
        private decimal _value;
        private int _lineNo;
        private bool _badRow;
        private string _reason = "";
        private string _why = "";
        private decimal _nQty;
        private decimal _nCost;
        private int _nExpiry;
        private readonly string[] _f = new string[14];

        // F-ITEM X(40) F-SITE X(10) F-WHSE X(20) F-LOC X(20) F-LOT X(30) F-SERIAL X(40) F-COND X(12) F-QTY-X X(30) F-UOM X(10)
        // F-COST-X X(30) F-EXPIRY-X X(10) F-OWNTYPE X(10) F-OWNCODE X(20) F-REF X(40)
        private static readonly int[] Widths = [40, 10, 20, 20, 30, 40, 12, 30, 10, 30, 10, 10, 20, 40];

        private string Item => _f[0];
        private string Site => _f[1];
        private string Loc => _f[3];
        private string Lot => _f[4];
        private string Serial => _f[5];
        private string OwnType => _f[11];
        private string OwnCode => _f[12];

        /// <summary>1100-ONE-ROW.</summary>
        public async Task OneRowAsync(string rec, CancellationToken ct)
        {
            _lineNo = (_lineNo + 1) % 10_000_000;
            if (Cob.Spaces(rec)) return;
            if (rec[0] == '*' || rec.StartsWith("ITEM", StringComparison.Ordinal)) return;
            Read++;
            Split(rec);
            Validate();
            if (_badRow)
            {
                Reject();
                return;
            }
            await PostRowAsync(ct);
        }

        /// <summary>1200-SPLIT-ROW: UNSTRING DELIMITED BY "," (no quote handling) into the 14 fields, then the defaults.</summary>
        private void Split(string rec)
        {
            var parts = rec.Split(',');
            for (var i = 0; i < _f.Length; i++) _f[i] = i < parts.Length ? Cob.X(parts[i], Widths[i]) : "";
            if (Cob.Spaces(_f[1])) _f[1] = a.Site;
            if (Cob.Spaces(_f[6])) _f[6] = "ACCEPTED";
            if (Cob.Spaces(_f[11])) _f[11] = "OWNED";
        }

        /// <summary>1300-VALIDATE-ROW: the first failure wins.</summary>
        private void Validate()
        {
            _badRow = false;
            _reason = "";
            _why = "";
            _nQty = _nCost = 0;
            _nExpiry = 0;
            if (Cob.Spaces(Item))
            {
                Bad("OPN0001", "Item code is missing on the opening row");
                return;
            }
            if (Cob.Spaces(Loc))
            {
                Bad("OPN0002", "Location is required for opening stock");
                return;
            }
            if (Cob.Spaces(_f[7]))
            {
                Bad("OPN0003", "Opening quantity is missing");
                return;
            }
            _nQty = Cob.Q(Col.NumVal(_f[7]));
            if (_nQty <= 0)
            {
                Bad("OPN0003", "Opening quantity must be greater than zero");
                return;
            }
            if (!Cob.Spaces(_f[9])) _nCost = CobolNumeric.Store(Col.NumVal(_f[9]), 11, 6);
            if (_nCost <= 0 && OwnType == "OWNED")
            {
                Bad("OPN0004", "Owned opening stock needs valuation evidence");
                return;
            }
            if (!Cob.Spaces(_f[10]))
            {
                _nExpiry = (int)(Math.Abs(decimal.Truncate(Col.NumVal(_f[10]))) % 100_000_000m);
                if (_nExpiry < 19000101 || _nExpiry > 29991231)
                {
                    Bad("OPN0005", "Expiry date is not a valid date");
                    return;
                }
            }
            if (OwnType != "OWNED" && Cob.Spaces(OwnCode)) Bad("OPN0006", "Third party stock must name its owner");
        }

        private void Bad(string reason, string why)
        {
            _reason = reason;
            _why = why;
            _badRow = true;
        }

        /// <summary>1400-POST-ROW.</summary>
        private async Task PostRowAsync(CancellationToken ct)
        {
            var lineEd = Cob.IntEd(_lineNo);
            var text = Cob.Str(200, "OPENSTK|", Cob.Trim(a.Co), "|", Cob.Trim(Item), "|", Cob.Trim(Site), "|", Cob.Trim(Loc), "|", Cob.Trim(Lot));
            var stem = CobolText.Fit(text, 27);
            var ca = CaHeader.Init(StkmoveFunctions.Opening, a.Co, Site, a.User, a.AsAt, Cob.Str(36, Cob.Trim(stem), "#", lineEd));
            var sm = new StkmoveCommarea
            {
                ItemCode = Item,
                Site = Site,
                Warehouse = _f[2],
                Location = Loc,
                LotCode = Lot,
                SerialNo = Serial,
                Condition = _f[6],
                OwnerType = OwnType,
                OwnerCode = OwnCode,
                Quantity = _nQty,
                UomCode = _f[8],
                UnitCost = _nCost,
                ExpiryDate = Cob.Date(_nExpiry),
                MovementDate = Cob.Date(a.AsAt),
                PostingDate = Cob.Date(a.AsAt),
                ReasonCode = "OPENING",
                SourceModule = "ITM",
                SourceDocType = "OPENING STOCK",
                SourceDocRef = _f[13],
                Reason = Cob.Str(200, "Opening stock load line ", lineEd),
            };
            if (a.Mode == "PREVIEW")
            {
                _why = "Not posted - PREVIEW run";
                _reason = "OPN0007";
                Rejected++;
                Reject();
                return;
            }
            var stkmove = ctx.Get<StkMoveProgram>();
            stkmove.RawExpiryDate = _nExpiry;
            await stkmove.RunAsync(ca, sm, ct);
            if (ca.ReturnCode < 8)
            {
                Posted++;
                _qty = CobolNumeric.Store(_qty + sm.StockQty, 13, 3);
                var value = CobolNumeric.Store(sm.StockQty * _nCost, 13, 2, rounded: true);
                _value = CobolNumeric.Store(_value + value, 13, 2);
                if (sm.Duplicate == "Y")
                {
                    _duplicate++;
                    _reason = "OPN0008";
                    _why = "Already loaded - duplicate opening receipt";
                    Reject();
                }
            }
            else
            {
                _reason = Cob.X(ca.ReasonCode, 8);
                _why = Cob.X(ca.Message, 70);
                Rejected++;
                Reject();
            }
        }

        /// <summary>1800-REJECT: one exception line pair (GENERATE DE-LINE).</summary>
        private void Reject()
        {
            if (_badRow) Rejected++;
            _xLine = _lineNo;
            _xItem = Item;
            _xLoc = Cob.Str(24, Cob.Trim(Site), "/", Cob.Trim(Loc));
            _xLot = Cob.X(Cob.Spaces(Serial) ? Lot : Serial, 20);
            _xQty = CobolNumeric.Store(_nQty, 11, 3);
            _xReason = Cob.X(_reason, 8);
            _xWhy = Cob.X(_why, 70);
            _rw!.Generate(_detail);
        }

        /// <summary>
        /// 0300 INITIATE R-OPENSTK: PAGE LIMIT 60, HEADING 1, FIRST DETAIL 11, LAST DETAIL 54, FOOTING 56, CONTROLS ARE FINAL;
        /// RH-COLHEAD (REPORT HEADING, LINE 8-10), DE-LINE (two lines) and CF-FINAL.
        /// </summary>
        public void Open(SpoolWriter spool, ReportHeading heading)
        {
            var dashes = new string('-', 66);
            _detail =
            [
                RwLine.Plus1(() => new RwText().Ed(1, _xLine, "Z(6)9").Src(10, _xItem, 40).Src(52, _xLoc, 24).Src(78, _xLot, 20)
                    .Ed(99, _xQty, "Z(8)9.999").Src(115, _xReason, 8).Text),
                RwLine.Plus1(() => new RwText().Src(10, _xWhy, 70).Text),
            ];
            _rw = new GnuReportWriter(spool.WriteLine, heading, 60, 1, 11, 54, controls: null,
                finalFooting:
                [
                    RwLine.PlusN(2, () => new RwText().Lit(1, dashes).Lit(67, dashes).Text),
                    RwLine.Plus1(() => new RwText().Lit(1, "Rows read:").Ed(14, Read, "Z(6)9").Lit(26, "Posted:").Ed(34, Posted, "Z(6)9")
                        .Lit(46, "Rejected:").Ed(56, Rejected, "Z(6)9").Lit(68, "Duplicate:").Ed(79, _duplicate, "Z(6)9").Text),
                    RwLine.Plus1(() => new RwText().Lit(1, "Opening quantity:").Ed(20, _qty, "Z(12)9.999").Lit(40, "Opening value:")
                        .Ed(55, _value, "Z(12)9.99").Text),
                    RwLine.PlusN(2, () => new RwText().Lit(1, "Opening stock is posted as a movement, never as an").Text),
                    RwLine.Plus1(() => new RwText().Lit(1, "edited balance, so layers tie to stock control.").Text),
                ],
                reportHeading:
                [
                    RwLine.At(8, () => new RwText().Lit(1, "Rows the load could not post - correct and re-run.").Text),
                    RwLine.At(9, () => new RwText().Lit(1, "Line").Lit(10, "Item").Lit(52, "Location").Lit(78, "Lot/serial ").Lit(100, "Quantity")
                        .Lit(115, "Reason").Text),
                    RwLine.At(10, () => new RwText().Lit(1, dashes).Lit(67, dashes).Text),
                ]);
            _rw.Initiate();
        }

        /// <summary>0400 TERMINATE R-OPENSTK.</summary>
        public void Terminate() => _rw!.Terminate();
    }
}
