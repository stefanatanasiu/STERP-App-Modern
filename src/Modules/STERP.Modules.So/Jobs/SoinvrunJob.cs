using System.Text.Json.Nodes;
using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.So.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Jobs;

/// <summary>
/// SOINVRUN (cobol/batch/so/SOINVRUN.cob; FR-STERP-SO-030): the sales invoicing run. Selects CONFIRMED despatch lines with
/// BilledQty &lt; DespatchedQty (at most 400), screens and withholds them, groups them per invoice customer, currency,
/// incoterms and despatch (and order when SO.INVOICE.PERORDER is Y) with a split before a 26th line, values each line from
/// the order line as accepted, and per group either prints it (exact MODE PREVIEW) or posts it through ARINVPST POST and
/// writes BilledQty, BillingStatus and an audit row back. Carried quirks: the first line after a flush is billed with the
/// flushed group's total quantity (SME-1110), the deposit lookup overwrites the next group's despatch text, RC 12 is sticky
/// (SME-1105), and ARINVPST's COMT commits SOINVRUN's open unit so the write-back autocommits (SME-013).
/// </summary>
internal static class SoinvrunJob
{
    public const string Code = "SOINVRUN";

    public static JobDefinition Definition => new()
    {
        JobCode = Code,
        Slice = SoBatch.Slice,
        SubmitOperationId = "so_submitSoinvrunRun",
        ParameterOrder = ["CO", "SITE", "CUST", "FROM", "TO", "USER", "MODE"],
        SelfRegistration = pre =>
        {
            var a = Arguments.Read(pre.Args);
            return SoBatch.JoblogStart(Code, a.Co, a.User, "", pre.CommandLine);
        },
        EndValues = SoBatch.End,
        Execute = ctx => new Run(ctx).MainAsync(),
    };

    /// <summary>0100-READ-ARGS: CO, SITE, USER, MODE (upper-cased by CMDARGS), FROM, TO fixed fields; CUST by key.</summary>
    private sealed record Arguments(string Co, string Site, string Cust, string User, string From, string To, string Mode)
    {
        public bool Preview => Mode == "PREVIEW";

        public static Arguments Read(CommandLineArgs a)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var mode = Cob.X(a.Mode, 10);
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "ARCLERK";
            if (Cob.Spaces(mode)) mode = "PREVIEW";
            return new Arguments(co, Cob.X(a.Site, 10), Cob.X(a.Get("CUST"), 20), user, Cob.X(a.From, 8), Cob.X(a.To, 8), mode);
        }
    }

    /// <summary>WS-BUF-LINE OCCURS 25.</summary>
    private sealed class BufLine
    {
        public long DespLineId, SoLineId;
        public string Order = "", Desp = "", Item = "", Desc = "", Uom = "", VatCode = "", Cc = "", Proj = "";
        public int OrderLine;
        public decimal Qty, Price, PricePer, Disc1, Disc2, Net, VatRate, Vat;
    }

    /// <summary>WS-EXC OCCURS 300.</summary>
    private sealed record Withheld(string Order, int Line, string Desp, string Item, decimal Qty, string Reason);

    private const string SelectText =
        "SELECT d.DespatchRef, d.CustomerCode, o.CurrencyCode, ISNULL(o.IncotermsRule,''), dl.OrderNumber, ISNULL(dl.OrderLineNum,0), " +
        "dl.ItemCode, ISNULL(dl.Description,''), dl.DespatchedQty, dl.BilledQty, ISNULL(dl.UomCode,''), l.UnitPrice, l.PricePerQty, " +
        "l.DiscountPct1, l.DiscountPct2, ISNULL(l.VatCode,''), ISNULL(l.VatRatePct,0), ISNULL(l.VatEvidenceRef,''), " +
        "ISNULL(l.DimCostCentre,''), ISNULL(l.DimProject,''), o.ReleaseStatus, CASE WHEN d.IsExport = 1 THEN 'Y' ELSE 'N' END, " +
        "d.ExportEvidenceStatus, CONVERT(char(8), d.PostingDate, 112), CONVERT(char(8), d.DespatchDate, 112), o.DepositReceivedGBP, " +
        "ISNULL(o.TermsCode,''), ISNULL(o.TermsSnapshot,''), ISNULL(o.SalespersonCode,''), o.FxRate, dl.DespatchLineId, " +
        "dl.SalesOrderLineId, ISNULL(o.InvoiceCustomerCode, o.CustomerCode), ISNULL(o.CustomerOrderRef,''), " +
        "ISNULL(o.CustomerVatRegNo,''), ISNULL(o.VatLocation,''), ISNULL(o.BillToAddressSnapshot,''), " +
        "ISNULL(o.DeliveryAddressSnapshot,''), l.LineType, ISNULL(l.VatOverrideReason,'') FROM so.DespatchLine dl " +
        "JOIN so.Despatch d ON d.DespatchId = dl.DespatchId JOIN so.SalesOrderLine l ON l.SalesOrderLineId = dl.SalesOrderLineId " +
        "JOIN so.SalesOrder o ON o.SalesOrderId = l.SalesOrderId WHERE d.CompanyCode = ? AND d.Status = 'CONFIRMED' " +
        "AND dl.BilledQty < dl.DespatchedQty AND (? = '' OR d.SiteCode = ?) AND (? = '' OR d.CustomerCode = ?) " +
        "AND (? = '' OR CONVERT(char(8),d.DespatchDate,112) >= ?) AND (? = '' OR CONVERT(char(8),d.DespatchDate,112) <= ?) " +
        "ORDER BY ISNULL(o.InvoiceCustomerCode,o.CustomerCode), o.CurrencyCode, ISNULL(o.IncotermsRule,''), dl.OrderNumber, " +
        "d.DespatchRef, dl.LineNum";

    private const int SelMax = 400;
    private const int BufMax = 25;
    private const int ExcMax = 300;
    private const string Qty = "Z(5)9.999";
    private const string Amt = "Z(7)9.99-";

    /// <summary>One run: the program's WORKING-STORAGE (run-scoped, carried between groups as the legacy run carried it).</summary>
    private sealed class Run(JobRunContext ctx)
    {
        private readonly CancellationToken _ct = ctx.Cancellation;
        private readonly ISqlRunner _sql = ctx.Get<ISqlRunner>();
        private readonly IUnitOfWork _uow = ctx.Get<IUnitOfWork>();
        private readonly IRoundingAllocator _rnd = ctx.Get<IRoundingAllocator>();
        private readonly IPeriodService _periodck = ctx.Get<IPeriodService>();
        private readonly IAuditWriter _audit = ctx.Get<IAuditWriter>();
        private readonly IArInvoicePostingService _arinvpst = ctx.Get<IArInvoicePostingService>();
        private Arguments _a = null!;
        private bool _perOrder;
        private int _today;
        private int _rc;
        private int _rowsOut;

        // SO-ROW: the fetch buffer, 40 slots of X(100)
        private string[] _row = new string[40];

        // WS-GRP
        private string _gKey = "", _gPrevKey = "";
        private string _gCust = "", _gInvCust = "", _gCurr = "", _gDelTerm = "", _gOrder = "", _gDesp = "", _gTerms = "", _gTermsSnap = "";
        private string _gSalesp = "", _gCustRef = "", _gCustVat = "", _gVatLoc = "", _gBillAddr = "", _gDelAddr = "";
        private decimal _gFx, _gDeposit;
        private int _gPostDate, _gDocDate;

        // WS-SCRATCH
        private bool _skip;
        private string _reason = "";
        private decimal _qty, _amt, _amt2, _depUsed, _depApply;
        private string _correl = "";
        private int _invSeq;

        private readonly List<BufLine> _buf = [];
        private readonly List<Withheld> _exc = [];

        // WS-DET and the CF-GRP SUM counters
        private string _dGroup = "", _dHeadCust = "", _dHeadCurr = "", _dHeadTerm = "", _dHeadInv = "";
        private string _dOrder = "", _dDesp = "", _dItem = "", _dVatc = "", _dReason = "";
        private int _dLine;
        private decimal _dQtyDesp, _dQtyBill, _dQtyHeld, _dNet, _dVat;
        private decimal _sBill, _sHeld, _sNet, _sVat;
        private string? _savedGroup;

        // WS-TOT
        private int _tInvoices, _tLines;
        private decimal _tQtyBill, _tQtyHeld, _tNet, _tVat, _tDeposit;
        private int _tXCredit, _tXVat, _tXExport, _tXPeriod, _tXOther;

        private GnuReportWriter _w = null!;
        private RwLine[] _detail = [];
        private readonly JsonArray _groups = [];

        public async Task<int> MainAsync()
        {
            var now = DateTime.Now;
            _a = Arguments.Read(ctx.Args);
            _today = CobolDate.ToYyyymmdd(now);
            ctx.Display("STERP-I-1400 SOINVRUN sales invoicing run for " + Cob.Trim(_a.Co) + " mode " + Cob.Trim(_a.Mode));

            // 0150-READ-PARMS (after 0200-JOB-START)
            var pm = await ctx.Get<IParameterService>().GetAsync(Caller(""), "SO.INVOICE.PERORDER", _today, "N", ct: _ct);
            var first = pm.ValueText.Length > 0 ? pm.ValueText[0] : ' ';
            _perOrder = first is 'Y' or 'y';

            // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS
            var heading = SoBatch.Heading(_a.Co, Code, "Sales invoicing run - control report", _a.User, now);
            heading.Subtitle = _a.Preview ? "PREVIEW - nothing is posted" : "COMMIT - invoices are posted by ARINVPST";
            heading.AsAtText = _perOrder ? "One invoice per order" : "Per customer, currency, terms";
            await SoBatch.ReadCompanyAsync(_sql, heading, _a.Co, _ct);
            if (!SoBatch.TryOpenSpool(ctx, Code, _a.Co, "STERP-E-1401 cannot build the spool path", "STERP-E-1402 cannot open ", out var spool))
            {
                return 12;
            }
            using (spool)
            {
                _w = Writer(spool, heading);
                _w.Initiate();
                await SelectAndRunAsync();
                ListWithheld();
                // 0400-CLOSE-SPOOL
                _w.Terminate();
            }
            ctx.Display("STERP-I-1411 " + SoBatch.Num(_rowsOut, 9) + " despatch line(s) examined");
            ctx.Display("STERP-I-1412 " + SoBatch.Num(_tInvoices, 7) + " invoice(s) posted");
            ctx.AnnounceSpool(spool.Path);
            ctx.RowsProcessed = _tLines;
            ctx.Result = new JsonObject
            {
                ["jobName"] = Code, ["companyCode"] = _a.Co, ["siteCode"] = _a.Site, ["mode"] = _a.Mode, ["perOrder"] = _perOrder,
                ["groups"] = _groups, ["withheld"] = new JsonArray(_exc.Select(x => (JsonNode)new JsonObject
                {
                    ["orderNumber"] = x.Order, ["lineNum"] = x.Line, ["despatchRef"] = x.Desp, ["itemCode"] = x.Item, ["quantity"] = x.Qty,
                    ["reason"] = x.Reason,
                }).ToArray()),
                ["linesExamined"] = _rowsOut, ["invoicesPosted"] = _tInvoices, ["linesBilled"] = _tLines, ["quantityBilled"] = _tQtyBill,
                ["netBilled"] = _tNet, ["vatBilled"] = _tVat, ["depositApplied"] = _tDeposit, ["quantityWithheld"] = _tQtyHeld,
                ["returnCode"] = _rc,
            };
            return _rc;
        }

        private KernelCaller Caller(string correlation) => new(_a.Co, "", _a.User, _today, correlation);

        private GnuReportWriter Writer(SpoolWriter spool, ReportHeading heading)
        {
            var group = new RwControl(() => _dGroup, v => _dGroup = v, 14)
            {
                Heading =
                [
                    RwLine.PlusN(2, () => new RwText().Lit(1, "Customer ").Put(10, _dHeadCust, 20).Lit(32, "Currency ").Put(41, _dHeadCurr, 3)
                        .Lit(47, "Delivery terms ").Put(62, _dHeadTerm, 3).Put(68, _dHeadInv, 22).Text),
                    RwLine.Plus1(() => new RwText().Lit(1, "Order").Lit(16, "Line").Lit(21, "Despatch").Lit(34, "Item").Lit(51, "Despatchd")
                        .Lit(61, "Billed   ").Lit(71, "Withheld ").Lit(81, "        Net").Lit(93, "VAT ").Lit(98, "        VAT")
                        .Lit(110, "Reason if withheld").Text),
                    RwLine.Plus1(() => new string('-', 132)),
                ],
                Footing =
                [
                    RwLine.Plus1(() => new RwText().Lit(34, "Invoice totals").Num(61, _sBill, Qty).Num(71, _sHeld, Qty).Num(81, _sNet, Amt)
                        .Num(98, _sVat, Amt).Text),
                ],
            };
            _detail =
            [
                RwLine.Plus1(() => new RwText().Put(1, _dOrder, 14).Num(16, _dLine, "ZZZ9").Put(21, _dDesp, 12).Put(34, _dItem, 16)
                    .Num(51, _dQtyDesp, Qty).Num(61, _dQtyBill, Qty).Num(71, _dQtyHeld, Qty).Num(81, _dNet, Amt).Put(93, _dVatc, 4)
                    .Num(98, _dVat, Amt).Put(110, _dReason, 23).Text),
            ];
            // RD R-SOINVRN: CONTROLS ARE FINAL D-GROUP, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 54
            return new GnuReportWriter(spool.WriteLine, heading, 60, 1, 9, 54, [group],
            [
                RwLine.PlusN(2, () => new RwText().Lit(1, "Invoices in this run").Num(28, _tInvoices, "Z(6)9").Lit(40, "lines billed")
                    .Num(55, _tLines, "Z(6)9").Lit(68, "quantity billed").Num(85, _tQtyBill, "Z(9)9.999").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "Net billed").Num(28, _tNet, "Z(9)9.99-").Lit(45, "VAT ").Num(50, _tVat, "Z(9)9.99-")
                    .Lit(68, "deposit applied").Num(85, _tDeposit, "Z(9)9.99-").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "Quantity withheld").Num(28, _tQtyHeld, "Z(9)9.999").Text),
                RwLine.PlusN(2, () => new RwText().Lit(1, "Withheld - credit hold").Num(28, _tXCredit, "Z(4)9").Lit(40, "missing VAT evidence")
                    .Num(63, _tXVat, "Z(4)9").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "Withheld - export proof").Num(28, _tXExport, "Z(4)9").Lit(40, "closed period")
                    .Num(63, _tXPeriod, "Z(4)9").Lit(75, "other").Num(90, _tXOther, "Z(4)9").Text),
                RwLine.PlusN(2, () => new RwText().Lit(1, "Agreed price, VAT code and terms come from the order").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "line as accepted.  A deposit settles, it is not sale").Text),
            ]);
        }

        /// <summary>GENERATE DE-LINE: a D-GROUP break presents CF-GRP with the sums so far, then the SUM counters restart.</summary>
        private void Generate()
        {
            var current = CobolText.Fit(_dGroup, 14);
            var broke = _savedGroup is not null && _savedGroup != current;
            _w.Generate(_detail);
            if (broke)
            {
                _sBill = _sHeld = _sNet = _sVat = 0;
            }
            _sBill += _dQtyBill;
            _sHeld += _dQtyHeld;
            _sNet += _dNet;
            _sVat += _dVat;
            _savedGroup = current;
        }

        private string Col(int n) => _row[n - 1];

        private decimal Num(int n) => CobolNumeric.NumVal(_row[n - 1]);

        /// <summary>1000-RUN: the selection is read to the end and held (at most 400 rows), then every row is processed.</summary>
        private async Task SelectAndRunAsync()
        {
            IReadOnlyList<SqlioRow> rows;
            try
            {
                rows = await _sql.FetchAllAsync(SelectText,
                [
                    SqlioParm.Text(_a.Co), SqlioParm.Text(_a.Site), SqlioParm.Text(_a.Site), SqlioParm.Text(_a.Cust), SqlioParm.Text(_a.Cust),
                    SqlioParm.Text(_a.From), SqlioParm.Text(_a.From), SqlioParm.Text(_a.To), SqlioParm.Text(_a.To),
                ], SoBatch.Widths(40, 100), _ct);
            }
            catch (SqlioException ex)
            {
                _rc = 12;
                ctx.Display("STERP-E-1403 " + CobolText.Fit(ex.DriverText, 100));
                return;
            }
            var selected = new List<string[]>();
            foreach (var r in rows)
            {
                if (selected.Count < SelMax)
                {
                    selected.Add(Enumerable.Range(0, 40).Select(i => CobolText.Fit(r.Raw(i), 100)).ToArray());
                }
                else
                {
                    if (_rc < 4) _rc = 4;
                    ctx.Display("STERP-W-1413 selection truncated at 400 despatch lines");
                    break;
                }
            }
            foreach (var s in selected)
            {
                _row = (string[])s.Clone();
                await OneRowAsync();
            }
            if (_buf.Count > 0) await FlushAsync();
        }

        /// <summary>1100-ONE-ROW.</summary>
        private async Task OneRowAsync()
        {
            _rowsOut++;
            await ScreenAsync();
            if (_skip)
            {
                Withhold();
                return;
            }
            // 1300-BUILD-KEY
            _gKey = _perOrder
                ? Col(33)[..20] + Col(3)[..3] + Col(4)[..3] + Col(5)[..30] + Col(1)[..30]
                : Col(33)[..20] + Col(3)[..3] + Col(4)[..3] + Col(1)[..30];
            if (_buf.Count > 0 && _gKey != _gPrevKey) await FlushAsync();
            if (_buf.Count >= BufMax) await FlushAsync();
            if (_buf.Count == 0) CaptureHeader();
            AddLine();
            _gPrevKey = _gKey;
        }

        /// <summary>1200-SCREEN-LINE / 1250-CHECK-PERIOD.</summary>
        private async Task ScreenAsync()
        {
            _skip = false;
            _reason = "";
            _qty = CobolNumeric.Store(Num(9) - Num(10), 11, 6);
            if (_qty <= 0)
            {
                Skip("Already billed");
                _tXOther = Cnt(_tXOther);
                return;
            }
            if (Col(21)[..10] == "CREDITHOLD")
            {
                Skip("Credit hold");
                _tXCredit = Cnt(_tXCredit);
                return;
            }
            if (Col(22)[..1] == "Y" && (Col(23)[..7] == "PENDING" || Col(23)[..7] == "OVERDUE" || Col(23)[..6] == "FAILED"))
            {
                Skip("Export proof outstanding");
                _tXExport = Cnt(_tXExport);
                return;
            }
            if ((Col(16)[..3] == "RC " || Col(16)[..3] == "DRC" || Col(16)[..3] == "EXP") && Cob.Spaces(Col(18)))
            {
                Skip("Missing VAT evidence");
                _tXVat = Cnt(_tXVat);
                return;
            }
            if (Cob.Spaces(Col(16)))
            {
                Skip("Missing VAT evidence");
                _tXVat = Cnt(_tXVat);
                return;
            }
            var posting = (int)CobolNumeric.Store(Num(24), 8, 0, signed: false);
            var document = (int)CobolNumeric.Store(Num(25), 8, 0, signed: false);
            var pc = await _periodck.CheckAsync(Caller(""), posting, document, document, "POST", ct: _ct);
            if (pc.Header.ReturnCode > 6)
            {
                Skip("Closed period");
                _tXPeriod = Cnt(_tXPeriod);
            }
        }

        private void Skip(string reason)
        {
            _skip = true;
            _reason = Cob.X(reason, 23);
        }

        private static int Cnt(int n) => (int)CobolNumeric.Store(n + 1, 5, 0);

        /// <summary>1900-WITHHOLD.</summary>
        private void Withhold()
        {
            _qty = CobolNumeric.Store(Num(9) - Num(10), 11, 6);
            if (_qty < 0) _qty = 0;
            _tQtyHeld = CobolNumeric.Store(_tQtyHeld + _qty, 13, 3);
            if (_exc.Count < ExcMax)
            {
                _exc.Add(new Withheld(Cob.X(Col(5), 14), (int)CobolNumeric.Store(Num(6), 4, 0, signed: false), Cob.X(Col(1), 12),
                    Cob.X(Col(7), 16), CobolNumeric.Store(_qty, 11, 3), _reason));
            }
        }

        /// <summary>1400-CAPTURE-HEADER.</summary>
        private void CaptureHeader()
        {
            _gCust = Cob.X(Col(2), 20);
            _gInvCust = Cob.X(Col(33), 20);
            _gCurr = Cob.X(Col(3), 3);
            _gDelTerm = Cob.X(Col(4), 3);
            _gOrder = Cob.X(Col(5), 30);
            _gDesp = Cob.X(Col(1), 30);
            _gTerms = Cob.X(Col(27), 12);
            _gTermsSnap = Cob.X(Col(28), 100);
            _gSalesp = Cob.X(Col(29), 20);
            _gCustRef = Cob.X(Col(34), 40);
            _gCustVat = Cob.X(Col(35), 15);
            _gVatLoc = Cob.X(Col(36), 3);
            _gBillAddr = Cob.X(Col(37), 300);
            _gDelAddr = Cob.X(Col(38), 300);
            _gFx = CobolNumeric.Store(Num(30), 9, 8);
            _gPostDate = (int)CobolNumeric.Store(Num(24), 8, 0, signed: false);
            _gDocDate = (int)CobolNumeric.Store(Num(25), 8, 0, signed: false);
            _gDeposit = CobolNumeric.Store(Num(26), 11, 2);
        }

        /// <summary>1500-ADD-LINE and 1600-VALUE-LINE.</summary>
        private void AddLine()
        {
            var b = new BufLine
            {
                DespLineId = (long)CobolNumeric.Store(Num(31), 12, 0, signed: false),
                SoLineId = (long)CobolNumeric.Store(Num(32), 12, 0, signed: false),
                Order = Cob.X(Col(5), 30),
                OrderLine = (int)CobolNumeric.Store(Num(6), 5, 0, signed: false),
                Desp = Cob.X(Col(1), 30),
                Item = Cob.X(Col(7), 40),
                Desc = Cob.X(Col(8), 60),
                Qty = _qty,
                Uom = Cob.X(Col(11), 10),
                Price = CobolNumeric.Store(Num(12), 11, 6),
                PricePer = CobolNumeric.Store(Num(13), 11, 6),
                Disc1 = CobolNumeric.Store(Num(14), 3, 4),
                Disc2 = CobolNumeric.Store(Num(15), 3, 4),
                VatCode = Cob.X(Col(16), 4),
                VatRate = CobolNumeric.Store(Num(17), 3, 4),
                Cc = Cob.X(Col(19), 20),
                Proj = Cob.X(Col(20), 20),
            };
            if (b.PricePer == 0) b.PricePer = 1;
            _buf.Add(b);
            // 1600-VALUE-LINE: extend, the two discounts in order, RNDALLOC ROUND to 2
            _amt = CobolNumeric.Store(b.Qty * b.Price / b.PricePer, 13, 6);
            _amt = CobolNumeric.Store(_amt * (1 - b.Disc1 / 100), 13, 6);
            _amt = CobolNumeric.Store(_amt * (1 - b.Disc2 / 100), 13, 6);
            Round2();
            b.Net = CobolNumeric.Store(_amt2, 11, 2);
            _amt = CobolNumeric.Store(b.Net * b.VatRate / 100, 13, 6);
            Round2();
            b.Vat = CobolNumeric.Store(_amt2, 11, 2);
        }

        /// <summary>1650-ROUND-2.</summary>
        private void Round2()
        {
            var r = _rnd.Round(_amt, 2);
            _amt2 = r.Header.ReturnCode > 4 ? _amt : CobolNumeric.Store(r.ResultValue, 13, 6);
        }

        /// <summary>3000-FLUSH-INVOICE.</summary>
        private async Task FlushAsync()
        {
            if (_buf.Count == 0) return;
            _invSeq = (int)CobolNumeric.Store(_invSeq + 1, 4, 0, signed: false);
            // 3100-BUILD-CORREL: WS-QTY becomes the group total (and stays so for the line being added - SME-1110)
            var keyId = _buf[0].DespLineId;
            _qty = 0;
            foreach (var b in _buf) _qty = CobolNumeric.Store(_qty + b.Qty, 11, 6);
            _correl = Cob.X("SOI-" + Cob.Trim(_a.Co) + "-" + Cob.Digits(keyId, 12) + "-" + CobolEdit.Field(_qty, "-(9)9.9(6)").Trim(' '), 36);
            await DepositAsync();
            _dHeadInv = "";
            if (_a.Preview)
            {
                _dHeadInv = "Preview only";
            }
            else
            {
                await PostInvoiceAsync();
            }
            PrintGroup();
            _buf.Clear();
            _gPrevKey = "";
        }

        /// <summary>3200-DEPOSIT: the SNGL fetch writes its one column into SO-COL(1) of the current row.</summary>
        private async Task DepositAsync()
        {
            _depApply = 0;
            _depUsed = 0;
            if (_gDeposit <= 0) return;
            try
            {
                var r = await _sql.SingleAsync("SELECT ISNULL(SUM(DepositAppliedGBP),0) FROM ar.SalesInvoice WHERE CompanyCode = ? AND " +
                    "SalesOrderNumber = ? AND DocumentType = 'INVOICE' AND Status IN ('APPROVED','POSTED')",
                    [SqlioParm.Text(_a.Co), SqlioParm.Text(_gOrder)], SoBatch.Widths(40, 100), _ct);
                if (r.Found)
                {
                    _row[0] = CobolText.Fit(r.Raw(0), 100);
                    _depUsed = CobolNumeric.Store(CobolNumeric.NumVal(_row[0]), 11, 2);
                }
            }
            catch (SqlioException)
            {
                // SQLIO-OK not set: nothing used
            }
            _amt = 0;
            foreach (var b in _buf)
            {
                _amt = CobolNumeric.Store(_amt + b.Net, 13, 6);
                _amt = CobolNumeric.Store(_amt + b.Vat, 13, 6);
            }
            _depApply = CobolNumeric.Store(_gDeposit - _depUsed, 11, 2);
            if (_depApply < 0) _depApply = 0;
            if (_depApply > _amt) _depApply = CobolNumeric.Store(_amt, 11, 2);
        }

        /// <summary>3300-POST-INVOICE (COMMIT): BEGN, ARINVPST POST, write-back, COMT.</summary>
        private async Task PostInvoiceAsync()
        {
            try
            {
                await _uow.BeginAsync(_ct);
            }
            catch (SqlioException)
            {
                _rc = 12;
                ctx.Display("STERP-E-1404 cannot start a transaction");
                return;
            }
            var ai = BuildInvoice();
            await _arinvpst.CallAsync(ai, _ct);
            if (ai.Header.ReturnCode > 4)
            {
                await RollbackAsync();
                _dHeadInv = Cob.X("REFUSED " + Cob.Trim(ai.Header.ReasonCode), 22);
                if (_rc < 4) _rc = 4;
                ctx.Display("STERP-W-1405 " + CobolText.Fit(ai.Header.Message, 200)[..90]);
                return;
            }
            await WriteBackAsync(ai);
            if (_rc > 8)
            {
                await RollbackAsync();
                return;
            }
            try
            {
                await _uow.CommitAsync(_ct);
            }
            catch (SqlioException)
            {
                _rc = 12;
                ctx.Display("STERP-E-1406 commit failed");
                return;
            }
            _tInvoices = (int)CobolNumeric.Store(_tInvoices + 1, 7, 0);
            _tDeposit = CobolNumeric.Store(_tDeposit + _depApply, 13, 2);
            _dHeadInv = Cob.X((ai.Duplicate == "Y" ? "Already " : "Invoice ") + Cob.Trim(ai.InvoiceNo), 22);
        }

        private async Task RollbackAsync()
        {
            try
            {
                await _uow.RollbackAsync(_ct);
            }
            catch (SqlioException)
            {
                // ROLB result not tested
            }
        }

        /// <summary>3400-CALL-ARINVPST: the area as SOINVRUN fills it.</summary>
        private ArinvpstArea BuildInvoice()
        {
            var ai = new ArinvpstArea();
            ai.Header.Prepare(new KernelCaller(_a.Co, _a.Site, _a.User, _today, _correl), InvoicePostingFunctions.Post);
            ai.Header.Program = "ARINVPST";
            ai.DocType = "INVOICE";
            ai.Customer = _gCust;
            ai.InvoiceCust = _gInvCust;
            ai.DocDate = _gDocDate;
            ai.TaxPoint = _gDocDate;
            ai.PostingDate = _gPostDate;
            ai.TermsCode = _gTerms;
            ai.TermsSnapshot = _gTermsSnap;
            ai.TermsSource = "ORDER";
            ai.Currency = _gCurr;
            ai.FxRate = _gFx;
            ai.FxRateDate = _gDocDate;
            ai.Source = "DESPATCH";
            ai.OrderRef = _gCustRef;
            ai.SoNumber = _gOrder;
            ai.DespatchRef = _gDesp;
            ai.Salesperson = _gSalesp;
            ai.BillAddress = _gBillAddr;
            ai.DeliveryAddr = _gDelAddr;
            ai.CustVatNo = _gCustVat;
            ai.VatLocation = _gVatLoc;
            ai.Simplified = "N";
            ai.DepositApply = _depApply;
            ai.LineCnt = _buf.Count;
            decimal net = 0, vat = 0;
            for (var i = 0; i < _buf.Count; i++)
            {
                var b = _buf[i];
                var l = ai.Lines[i];
                l.LItem = b.Item;
                l.LDesc = b.Desc;
                l.LQty = CobolNumeric.Store(b.Qty, 9, 6);
                l.LUom = b.Uom;
                l.LPrice = b.Price;
                l.LDiscPct = b.Disc1;
                l.LCharge = 0;
                l.LNet = b.Net;
                l.LVatCode = b.VatCode;
                l.LVatRate = b.VatRate;
                l.LVatAmt = b.Vat;
                l.LCostCentre = b.Cc;
                l.LProject = b.Proj;
                l.LSoLine = b.OrderLine;
                net = CobolNumeric.Store(net + b.Net, 11, 2);
                vat = CobolNumeric.Store(vat + b.Vat, 11, 2);
            }
            ai.Net = net;
            ai.Vat = vat;
            ai.Gross = CobolNumeric.Store(net + vat, 11, 2);
            if (_gCurr == "GBP" || _gFx == 0 || _gFx == 1)
            {
                ai.NetGbp = ai.Net;
                ai.VatGbp = ai.Vat;
                ai.GrossGbp = ai.Gross;
            }
            else
            {
                ai.NetGbp = CobolNumeric.Store(ai.Net / _gFx, 11, 2, rounded: true);
                ai.VatGbp = CobolNumeric.Store(ai.Vat / _gFx, 11, 2, rounded: true);
                ai.GrossGbp = CobolNumeric.Store(ai.Gross / _gFx, 11, 2, rounded: true);
            }
            return ai;
        }

        /// <summary>3500-WRITE-BACK: per line the despatch line, order line and audit; then the order's BillingStatus.</summary>
        private async Task WriteBackAsync(ArinvpstArea ai)
        {
            for (var i = 0; i < _buf.Count && _rc <= 8; i++)
            {
                var b = _buf[i];
                await DespatchLineAsync(b);
                await OrderLineAsync(b);
                await AuditLineAsync(b, ai);
            }
            if (_rc > 8) return;
            // 3650-ORDER-STATUS
            try
            {
                await _sql.ExecAsync("UPDATE o SET BillingStatus = CASE WHEN EXISTS (SELECT 1 FROM so.SalesOrderLine x WHERE " +
                    "x.SalesOrderId = o.SalesOrderId AND x.Status = 'OPEN' AND x.BilledQty < x.DespatchedQty) THEN 'PARTBILLED' ELSE " +
                    "'BILLED' END, ModifiedBy = ?, ModifiedAt = SYSDATETIME() FROM so.SalesOrder o WHERE o.CompanyCode = ? AND " +
                    "o.OrderNumber = ?", [SqlioParm.Text(_a.User), SqlioParm.Text(_a.Co), SqlioParm.Text(_gOrder)], _ct);
            }
            catch (SqlioException ex)
            {
                _rc = 12;
                ctx.Display("STERP-E-1410 " + CobolText.Fit(ex.DriverText, 90));
            }
        }

        /// <summary>3550-ONE-DESP-LINE: BilledQty advanced, never past DespatchedQty; exactly one row or E-1408.</summary>
        private async Task DespatchLineAsync(BufLine b)
        {
            SqlioExecResult r;
            try
            {
                r = await _sql.ExecAsync("UPDATE so.DespatchLine SET BilledQty = BilledQty + ? WHERE DespatchLineId = ? AND " +
                    "BilledQty + ? <= DespatchedQty", [SqlioParm.Number(b.Qty), SqlioParm.Digits(b.DespLineId, 12), SqlioParm.Number(b.Qty)], _ct);
            }
            catch (SqlioException ex)
            {
                _rc = 12;
                ctx.Display("STERP-E-1407 " + CobolText.Fit(ex.DriverText, 90));
                return;
            }
            if (r.RowCount != 1)
            {
                _rc = 12;
                ctx.Display("STERP-E-1408 despatch line already billed");
                return;
            }
            _tQtyBill = CobolNumeric.Store(_tQtyBill + b.Qty, 13, 3);
            _tNet = CobolNumeric.Store(_tNet + b.Net, 13, 2);
            _tVat = CobolNumeric.Store(_tVat + b.Vat, 13, 2);
            _tLines = (int)CobolNumeric.Store(_tLines + 1, 7, 0);
        }

        /// <summary>3600-ONE-ORDER-LINE (no row-count test).</summary>
        private async Task OrderLineAsync(BufLine b)
        {
            if (_rc > 8) return;
            try
            {
                await _sql.ExecAsync("UPDATE so.SalesOrderLine SET BilledQty = BilledQty + ? WHERE SalesOrderLineId = ? AND " +
                    "BilledQty + ? <= DespatchedQty", [SqlioParm.Number(b.Qty), SqlioParm.Digits(b.SoLineId, 12), SqlioParm.Number(b.Qty)], _ct);
            }
            catch (SqlioException ex)
            {
                _rc = 12;
                ctx.Display("STERP-E-1409 " + CobolText.Fit(ex.DriverText, 90));
            }
        }

        /// <summary>3700-AUDIT-LINE: the invoice number recorded against the despatch line (result not tested).</summary>
        private async Task AuditLineAsync(BufLine b, ArinvpstArea ai)
        {
            if (_rc > 8) return;
            await _audit.WriteAsync(Caller(_correl), new AuditlogArea
            {
                EntityType = "so.DespatchLine",
                EntityKey = Cob.Digits(b.DespLineId, 12),
                Action = "INVOICE",
                FieldName = "BilledQty",
                NewValue = CobolEdit.Field(b.Qty, "-(9)9.9(6)").Trim(' '),
                Reason = Cob.Str(200, "Billed on invoice ", Cob.Trim(ai.InvoiceNo), " order ", Cob.Trim(b.Order)),
                AuthorityRef = Cob.X(ai.InvoiceNo, 40),
                Source = Code,
                Sensitive = "N",
            }, _ct);
        }

        /// <summary>3800-PRINT-GROUP.</summary>
        private void PrintGroup()
        {
            _dGroup = "INV" + Cob.Digits(_invSeq, 4);
            _dHeadCust = _gInvCust;
            _dHeadCurr = _gCurr;
            _dHeadTerm = _gDelTerm;
            var lines = new JsonArray();
            foreach (var b in _buf)
            {
                _dOrder = Cob.X(b.Order, 14);
                _dLine = (int)CobolNumeric.Store(b.OrderLine, 4, 0, signed: false);
                _dDesp = Cob.X(b.Desp, 12);
                _dItem = Cob.X(b.Item, 16);
                _dQtyDesp = CobolNumeric.Store(b.Qty, 11, 3);
                _dQtyBill = _dQtyDesp;
                _dQtyHeld = 0;
                _dNet = b.Net;
                _dVatc = b.VatCode;
                _dVat = b.Vat;
                _dReason = "";
                Generate();
                lines.Add(new JsonObject
                {
                    ["orderNumber"] = b.Order, ["orderLine"] = b.OrderLine, ["despatchRef"] = b.Desp, ["itemCode"] = b.Item, ["quantity"] = b.Qty,
                    ["net"] = b.Net, ["vatCode"] = b.VatCode, ["vat"] = b.Vat,
                });
            }
            _groups.Add(new JsonObject
            {
                ["group"] = _dGroup, ["invoiceCustomer"] = _gInvCust, ["currency"] = _gCurr, ["deliveryTerms"] = _gDelTerm,
                ["outcome"] = _dHeadInv, ["correlationId"] = _correl, ["depositApplied"] = _depApply, ["lines"] = lines,
            });
        }

        /// <summary>6000-LIST-WITHHELD.</summary>
        private void ListWithheld()
        {
            if (_exc.Count == 0) return;
            _dGroup = "WITHHELD";
            _dHeadCust = "";
            _dHeadCurr = "";
            _dHeadTerm = "";
            _dHeadInv = "Not invoiced";
            foreach (var x in _exc)
            {
                _dOrder = x.Order;
                _dLine = x.Line;
                _dDesp = x.Desp;
                _dItem = x.Item;
                _dQtyDesp = x.Qty;
                _dQtyBill = 0;
                _dQtyHeld = x.Qty;
                _dNet = 0;
                _dVat = 0;
                _dVatc = "";
                _dReason = x.Reason;
                Generate();
            }
        }
    }
}
