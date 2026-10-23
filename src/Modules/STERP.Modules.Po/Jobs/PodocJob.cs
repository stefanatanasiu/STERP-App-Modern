using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Jobs;

/// <summary>
/// PODOC (cobol/batch/po/PODOC.cob; FR-STERP-PO-044): the purchase order document (RPT-024) - the approved version with its
/// content hash and approver (NOT APPROVED / AMENDED SINCE APPROVAL turn it into a draft, RC 4), the order lines and the
/// history appendix (receipts, returns, import entries, landed cost, amendments); earlier OK/WARNING runs for the same order in
/// core.JobRun make it a REPRINT. ORDER is required (RC 12 STERP-E-1740, checked before the program's JOBLOG START); an
/// unknown order ends RC 16.
/// </summary>
internal static class PodocJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "PODOC",
        Slice = "STERP-PO",
        SubmitOperationId = "po_runPodoc",
        ParameterOrder = ["CO", "ORDER", "USER"],
        SelfRegistration = pre =>
        {
            var a = Arguments.From(pre.Args);
            return Cob.Spaces(a.Order) ? null : PoBatch.JoblogStart("PODOC", a.Co, a.User, pre.Args.Text);
        },
        EndValues = (ctx, exit) => PoBatch.JoblogEnd(exit, ctx.RowsProcessed ?? 0, ctx.SpoolFile),
        Execute = RunAsync,
    };

    private sealed record Arguments(string Co, string Order, string User)
    {
        public static Arguments From(Sterp.Platform.Kernel.Jobs.CommandLineArgs a)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var order = a.Get("ORDER") is { } o ? Cob.X(o, 30) : "";
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "BUYER1";
            return new Arguments(co, order, user);
        }
    }

    /// <summary>WS-HDR.</summary>
    private sealed class Header
    {
        public string SuppCode = "";
        public string SuppName = "";
        public string OrderType = "";
        public string OrderDate = "";
        public string Buyer = "";
        public string Currency = "";
        public string Terms = "";
        public string Incoterms = "";
        public string Delivery = "";
        public string TcsVersion = "";
        public string SuppRef = "";
        public string Status = "";
        public string DelivStatus = "";
        public string ApprovedBy = "";
        public string ApprovedAt = "";
        public string ContentHash = "";
        public string ApprovalId = "";
        public decimal Net;
        public decimal Vat;
        public decimal Gross;
        public decimal Commit;
        public string IssueMark = "";
        public string VersionNote = "";
    }

    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var now = PoBatch.Now();
        var a = Arguments.From(ctx.Args);
        var rc = 0;
        var lineCount = 0;
        var eventCount = 0;
        if (Cob.Spaces(a.Order))
        {
            rc = 12;
            ctx.Display("STERP-E-1740 ORDER=<order number> is required");
        }
        ctx.Display("STERP-I-1741 PODOC order document " + Cob.Trim(a.Order));
        if (rc > 4) return Finish(ctx, rc, lineCount);
        var sql = PoBatch.Sql(ctx);
        var h = new Header();
        rc = await ReadOrderAsync(ctx, sql, a, h, rc, ct);
        if (rc > 4) return Finish(ctx, rc, lineCount);
        var prior = await CountPrintsAsync(sql, a, h, ct);
        var spool = PoBatch.OpenSpool(ctx, "PODOC", a.Co, out var pathFailed, out var spoolName);
        if (!pathFailed) ctx.SpoolFile = spoolName;
        if (spool is null)
        {
            ctx.Display(pathFailed ? "STERP-E-1743 cannot build the spool path" : "STERP-E-1744 cannot open " + CobolText.Fit(spoolName, 200));
            return Finish(ctx, 12, lineCount);
        }
        var heading = await PoBatch.HeadingAsync(sql, a.Co, "RPT-024", "Purchase order " + Cob.Trim(a.Order),
            Cob.X("Supplier " + Cob.Trim(h.SuppCode) + " - " + Cob.Trim(h.SuppName), 60),
            "Printed " + now.ToString("dd'/'MM'/'yyyy", System.Globalization.CultureInfo.InvariantCulture), a.User, now, ct);
        using (spool)
        {
            var rw = PoBatch.Report(spool, heading, null, FinalFooting(h));
            rw.Initiate();
            DocumentHeader(rw, h);
            (rc, lineCount) = await PrintLinesAsync(ctx, sql, a, rw, rc, ct);
            (rc, eventCount) = await AppendixAsync(ctx, sql, a, rw, rc, ct);
            rw.Terminate();
        }
        ctx.Display("STERP-I-1747 " + PoBatch.Disp(lineCount, 5) + " order line(s), " + PoBatch.Disp(eventCount, 5) + " history event(s)");
        if (prior > 0)
        {
            ctx.Display("STERP-W-1748 reprint number " + PoBatch.Disp(prior, 5) + " of this order");
        }
        ctx.AnnounceSpool(spoolName);
        return Finish(ctx, rc, lineCount);
    }

    private static int Finish(JobRunContext ctx, int rc, int lineCount)
    {
        ctx.RowsProcessed = lineCount;
        return rc;
    }

    /// <summary>0600-READ-ORDER: the approved version (RC 4 for a draft or a pending re-approval, RC 16 when not found).</summary>
    private static async Task<int> ReadOrderAsync(JobRunContext ctx, PoSql sql, Arguments a, Header h, int rc, CancellationToken ct)
    {
        var r = await sql.Sngl(
            "SELECT o.SupplierCode, ISNULL((SELECT TOP 1 s.LegalName FROM ap.Supplier s WHERE s.CompanyCode = o.CompanyCode AND " +
            "s.SupplierCode = o.SupplierCode),''), o.OrderType, CONVERT(char(8),o.OrderDate,112), ISNULL(o.BuyerUser,''), " +
            "o.CurrencyCode, ISNULL(o.TermsSnapshot,ISNULL(o.TermsCode,'')), ISNULL(o.IncotermsRule,'') + ' ' + " +
            "ISNULL(o.IncotermsNamedPlace,''), ISNULL(o.DeliveryAddressSnapshot,''), ISNULL(o.TermsAndConditionsVersion,''), " +
            "ISNULL(o.SupplierReference,''), o.Status, o.DeliveryStatus, o.MatchStatus, ISNULL(o.ApprovedBy,''), " +
            "ISNULL(CONVERT(char(8),o.ApprovedAt,112),''), ISNULL(o.ContentHash,''), ISNULL(CAST(o.ApprovalId AS varchar(20)),''), " +
            "CAST(o.NetAmount AS varchar(30)), CAST(o.VatAmount AS varchar(30)), CAST(o.GrossAmount AS varchar(30)), " +
            "CAST(o.CommitmentGBP AS varchar(30)), CAST((SELECT COUNT(*) FROM po.OrderAmendment a WHERE a.CompanyCode = " +
            "o.CompanyCode AND a.OrderNumber = o.OrderNumber AND a.RequiresReapproval = 1 AND a.Status = 'OPEN') AS varchar(10)) " +
            "FROM po.PurchaseOrder o WHERE o.CompanyCode = ? AND o.OrderNumber = ?",
            [PoSql.Tx(a.Co), PoSql.Tx(a.Order)], Enumerable.Repeat(100, 23).ToArray(), ct);
        if (!r.Ok)
        {
            ctx.Display("STERP-E-1742 order " + Cob.Trim(a.Order) + " not found in " + Cob.Trim(a.Co));
            return 16;
        }
        var row = r.Row;
        h.SuppCode = Cob.X(row.Text(0), 20);
        h.SuppName = Cob.X(row.Text(1), 60);
        h.OrderType = Cob.X(row.Text(2), 12);
        h.OrderDate = PoBatch.DateEd(Date8(row.Raw(3)));
        h.Buyer = Cob.X(row.Text(4), 20);
        h.Currency = Cob.X(row.Text(5), 3);
        h.Terms = Cob.X(row.Text(6), 60);
        h.Incoterms = Cob.X(row.Text(7), 40);
        h.Delivery = Cob.X(row.Text(8), 80);
        h.TcsVersion = Cob.X(row.Text(9), 20);
        h.SuppRef = Cob.X(row.Text(10), 40);
        h.Status = Cob.X(row.Text(11), 14);
        h.DelivStatus = Cob.X(row.Text(12), 14);
        h.ApprovedBy = Cob.X(row.Text(14), 20);
        h.ApprovedAt = PoBatch.DateEd(Cob.Spaces(CobolText.Fit(row.Raw(15), 8)) ? 0 : Date8(row.Raw(15)));
        h.ContentHash = Cob.X(row.Text(16), 64);
        h.ApprovalId = Cob.X(row.Text(17), 20);
        h.Net = Cob.T(row.NumVal(18), 13, 2);
        h.Vat = Cob.T(row.NumVal(19), 13, 2);
        h.Gross = Cob.T(row.NumVal(20), 13, 2);
        h.Commit = Cob.T(row.NumVal(21), 13, 2);
        var pendingAmend = Cob.T(row.NumVal(22), 5, 0);
        if (Cob.Spaces(h.ApprovedBy))
        {
            h.VersionNote = "NOT APPROVED - this is a draft, not an order";
            rc = 4;
        }
        else if (pendingAmend > 0)
        {
            h.VersionNote = "AMENDED SINCE APPROVAL - re-approval due";
            rc = 4;
        }
        else
        {
            h.VersionNote = "Approved version as shown by the hash";
        }
        if (Cob.Spaces(h.ContentHash)) h.ContentHash = "(no content hash held)";
        return rc;
    }

    /// <summary>MOVE PO-COL(n)(1:8) TO WS-WORK-DATE (PIC 9(8)).</summary>
    private static int Date8(string raw) => (int)Cob.T(Cob.NumVal(CobolText.Fit(raw, 8)), 8, 0, signed: false);

    /// <summary>0650-COUNT-PRINTS: earlier OK/WARNING PODOC runs whose arguments name the order (the current run is RUNNING).</summary>
    private static async Task<int> CountPrintsAsync(PoSql sql, Arguments a, Header h, CancellationToken ct)
    {
        var like = Cob.X("%ORDER=" + Cob.Trim(a.Order) + "%", 60);
        var r = await sql.Sngl("SELECT CAST(COUNT(*) AS varchar(10)), ISNULL(CONVERT(char(8),MIN(StartedAt),112),'') FROM core.JobRun " +
            "WHERE JobName = 'PODOC' AND CompanyCode = ? AND Arguments LIKE ? AND Status IN ('OK','WARNING')",
            [PoSql.Tx(a.Co), PoSql.Tx(like)], [100, 100], ct);
        var prior = 0;
        var firstIssue = "";
        if (r.Ok)
        {
            prior = (int)Cob.T(r.Row.NumVal(0), 5, 0);
            firstIssue = CobolText.Fit(r.Row.Raw(1), 8);
        }
        if (prior == 0)
        {
            h.IssueMark = Cob.X("ORIGINAL ISSUE - order " + Cob.Trim(a.Order), 60);
        }
        else
        {
            var date = PoBatch.DateEd(Cob.Spaces(firstIssue) ? 0 : Date8(firstIssue));
            h.IssueMark = Cob.X("*** REPRINT No " + PoBatch.Ed(prior, "Z(4)9").Trim(' ') + " - first issued " + date + " ***", 60);
        }
        return prior;
    }

    /// <summary>DE-DOCHDR.</summary>
    private static void DocumentHeader(GnuReportWriter rw, Header h) => rw.Generate(
        RwLine.Plus1(() => PoBatch.Line().Put(1, h.IssueMark, 60).Put(70, h.VersionNote, 60).Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(1, PoBatch.Rule('=')).Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(1, "Supplier  : ").Put(13, h.SuppCode, 20).Put(34, h.SuppName, 50).Lit(90, "Order type: ")
            .Put(102, h.OrderType, 12).Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(1, "Order date: ").Put(13, h.OrderDate, 10).Lit(34, "Buyer:  ").Put(42, h.Buyer, 20)
            .Lit(90, "Currency  : ").Put(102, h.Currency, 3).Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(1, "Terms     : ").Put(13, h.Terms, 60).Lit(90, "Supp ref  : ").Put(102, h.SuppRef, 30).Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(1, "Incoterms : ").Put(13, h.Incoterms, 40).Lit(90, "T and Cs  : ").Put(102, h.TcsVersion, 20).Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(1, "Deliver to: ").Put(13, h.Delivery, 80).Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(1, "Approved  : ").Put(13, h.ApprovedBy, 20).Put(34, h.ApprovedAt, 10).Lit(46, "Approval  : ")
            .Put(58, h.ApprovalId, 20).Lit(90, "Status    : ").Put(102, h.Status, 14).Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(1, "Version   : ").Put(13, h.ContentHash, 64).Lit(90, "Delivery  : ").Put(102, h.DelivStatus, 14).Text));

    /// <summary>DE-SECT.</summary>
    private static void Section(GnuReportWriter rw, string text) => rw.Generate(
        RwLine.PlusN(2, () => PoBatch.Line().Put(1, text, 80).Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(1, PoBatch.Rule('-')).Text));

    /// <summary>1000-PRINT-LINES.</summary>
    private static async Task<(int Rc, int Lines)> PrintLinesAsync(JobRunContext ctx, PoSql sql, Arguments a, GnuReportWriter rw, int rc,
        CancellationToken ct)
    {
        Section(rw, "ORDER LINES   Line / item / description / ordered / received / outstanding / price / net");
        var r = await sql.Cursor(
            "SELECT l.LineNum, ISNULL(l.ItemCode,''), l.Description, CAST(l.OrderedQty AS varchar(30)), CAST(l.ReceivedQty AS " +
            "varchar(30)), CAST(l.OrderedQty - l.CancelledQty - l.ReceivedQty AS varchar(30)), CAST(l.UnitPrice AS varchar(30)), " +
            "CAST(l.NetAmount AS varchar(30)), ISNULL(l.VatCode,''), ISNULL(CONVERT(char(8),(SELECT MIN(s.CurrentConfirmedDate) FROM " +
            "po.PurchaseSchedule s WHERE s.PurchaseOrderLineId = l.PurchaseOrderLineId),112),''), l.Status, ISNULL(l.AccountCode,'') " +
            "FROM po.PurchaseOrderLine l WHERE l.CompanyCode = ? AND l.OrderNumber = ? ORDER BY l.LineNum",
            [PoSql.Tx(a.Co), PoSql.Tx(a.Order)], Enumerable.Repeat(100, 12).ToArray(), ct);
        if (r.Error)
        {
            ctx.Display("STERP-E-1745 " + Cob.Trim(r.Err));
            return (12, 0);
        }
        var count = 0;
        foreach (var row in r.Rows)
        {
            var num = Cob.T(row.NumVal(0), 4, 0, signed: false);
            var promise = PoBatch.DateEd(Cob.Spaces(CobolText.Fit(row.Raw(9), 8)) ? 0 : Date8(row.Raw(9)));
            count++;
            var item = CobolText.Fit(row.Raw(1), 20);
            var desc = CobolText.Fit(row.Raw(2), 40);
            var ordered = Cob.T(row.NumVal(3), 11, 3);
            var received = Cob.T(row.NumVal(4), 11, 3);
            var outstanding = Cob.T(row.NumVal(5), 11, 3);
            var price = Cob.T(row.NumVal(6), 9, 4);
            var net = Cob.T(row.NumVal(7), 11, 2);
            var vatCode = CobolText.Fit(row.Raw(8), 6);
            var status = CobolText.Fit(row.Raw(10), 12);
            var account = CobolText.Fit(row.Raw(11), 12);
            rw.Generate(
                RwLine.Plus1(() => PoBatch.Line().Num(1, num, "ZZZ9").Put(6, item, 20).Put(27, desc, 40).Num(68, ordered, "Z(6)9.999")
                    .Num(79, received, "Z(6)9.999").Num(90, outstanding, "Z(6)9.999").Num(101, price, "Z(5)9.9999").Num(113, net, "Z(9)9.99-")
                    .Put(127, vatCode, 6).Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(6, "Promise: ").Put(15, promise, 10).Lit(27, "Account: ").Put(36, account, 12)
                    .Lit(50, "Status: ").Put(58, status, 12).Text));
        }
        return (rc, count);
    }

    /// <summary>2000-APPENDIX: receipts, returns, import entries, landed cost and amendments in one union (RC 4 when it fails).</summary>
    private static async Task<(int Rc, int Events)> AppendixAsync(JobRunContext ctx, PoSql sql, Arguments a, GnuReportWriter rw, int rc,
        CancellationToken ct)
    {
        Section(rw, "ORDER HISTORY   receipts, returns, landed cost and amendments");
        var r = await sql.Cursor(
            "SELECT 'RECEIPT', r.ReceiptRef, CONVERT(char(8),r.PhysicalDate,112), CAST(rl.ReceivedQty AS varchar(30)), " +
            "CAST(rl.ProvisionalValueGBP AS varchar(30)), LEFT('line ' + CAST(rl.PurchaseOrderLine AS varchar(6)) + ' ' + " +
            "rl.QualityStatus + ' note ' + ISNULL(r.DeliveryNoteRef,'-'),58) FROM po.ReceiptLine rl JOIN po.Receipt r ON r.ReceiptId = " +
            "rl.ReceiptId WHERE r.CompanyCode = ? AND rl.PurchaseOrderNumber = ? UNION ALL SELECT 'RETURN', x.ReturnRef, " +
            "CONVERT(char(8),x.ReturnDate,112), CAST(ISNULL((SELECT SUM(xl.ReturnedQty) FROM po.SupplierReturnLine xl WHERE " +
            "xl.SupplierReturnId = x.SupplierReturnId),0) AS varchar(30)), CAST(x.ExpectedCreditGBP AS varchar(30)), LEFT(x.Status + " +
            "' ' + ISNULL(x.ReasonCode,'') + ' credit ' + ISNULL(x.CreditNoteRef,'awaited'),58) FROM po.SupplierReturn x WHERE " +
            "x.CompanyCode = ? AND ISNULL(x.PurchaseOrderNumber,'') = ? UNION ALL SELECT 'IMPORT', ie.ImportEntryRef, " +
            "CONVERT(char(8),ie.EntryDate,112), 0, CAST(ie.DutyAmountGBP AS varchar(30)), LEFT('duty capitalised, import VAT ' + " +
            "ie.ImportVatMethod + ' ' + ie.StatementStatus,58) FROM po.ImportEntry ie WHERE ie.CompanyCode = ? AND " +
            "ISNULL(ie.PurchaseOrderNumber,'') = ? UNION ALL SELECT 'LANDEDCOST', lc.ChargeRef, ISNULL(CONVERT(char(8)," +
            "lc.AllocationDate,112),CONVERT(char(8),lc.CreatedAt,112)), 0, CAST(ISNULL(lc.ActualAmountGBP,lc.EstimatedAmountGBP) AS " +
            "varchar(30)), LEFT(lc.Category + ' ' + lc.Status + ' basis ' + lc.AllocationBasis,58) FROM cst.LandedCost lc WHERE " +
            "lc.CompanyCode = ? AND EXISTS (SELECT 1 FROM cst.LandedCostAllocation la JOIN po.Receipt rr ON rr.ReceiptRef = " +
            "la.ReceiptRef AND rr.CompanyCode = lc.CompanyCode WHERE la.LandedCostId = lc.LandedCostId AND " +
            "ISNULL(rr.PurchaseOrderNumber,'') = ?) UNION ALL SELECT 'AMENDMENT', 'AMD ' + CAST(a.AmendmentNo AS varchar(6)), " +
            "CONVERT(char(8),a.AmendedAt,112), 0, 0, LEFT(a.FieldName + ' ' + ISNULL(a.OriginalValue,'') + ' -> ' + " +
            "ISNULL(a.RevisedValue,''),58) FROM po.OrderAmendment a WHERE a.CompanyCode = ? AND a.OrderNumber = ? ORDER BY 3, 1, 2",
            [PoSql.Tx(a.Co), PoSql.Tx(a.Order), PoSql.Tx(a.Co), PoSql.Tx(a.Order), PoSql.Tx(a.Co), PoSql.Tx(a.Order), PoSql.Tx(a.Co),
             PoSql.Tx(a.Order), PoSql.Tx(a.Co), PoSql.Tx(a.Order)], Enumerable.Repeat(100, 6).ToArray(), ct);
        if (r.Error)
        {
            ctx.Display("STERP-W-1746 " + Cob.Trim(r.Err));
            return (rc < 4 ? 4 : rc, 0);
        }
        var count = 0;
        foreach (var row in r.Rows)
        {
            var date = PoBatch.DateEd(Cob.Spaces(CobolText.Fit(row.Raw(2), 8)) ? 0 : Date8(row.Raw(2)));
            count++;
            Event(rw, CobolText.Fit(row.Raw(0), 12), CobolText.Fit(row.Raw(1), 20), date, Cob.T(row.NumVal(3), 11, 3), Cob.T(row.NumVal(4), 11, 2),
                CobolText.Fit(row.Raw(5), 58));
        }
        if (count == 0)
        {
            Event(rw, "NONE", "", "", 0, 0, "Nothing has happened to this order yet");
        }
        return (rc, count);
    }

    /// <summary>DE-EVENT.</summary>
    private static void Event(GnuReportWriter rw, string type, string reference, string date, decimal qty, decimal value, string note) =>
        rw.Generate(RwLine.Plus1(() => PoBatch.Line().Put(1, type, 12).Put(14, reference, 20).Put(35, date, 10).Num(46, qty, "Z(6)9.999")
            .Num(58, value, "Z(9)9.99-").Put(73, note, 58).Text));

    /// <summary>CF-FINAL (presented by TERMINATE: DE-DOCHDR was always generated).</summary>
    private static RwLine[] FinalFooting(Header h) =>
    [
        RwLine.PlusN(2, () => PoBatch.Line().Lit(1, PoBatch.Rule('=')).Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(60, "Net of order    : ").Num(78, h.Net, "Z(11)9.99-").Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(60, "Expected VAT    : ").Num(78, h.Vat, "Z(11)9.99-").Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(60, "Gross of order  : ").Num(78, h.Gross, "Z(11)9.99-").Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(60, "Commitment held : ").Num(78, h.Commit, "Z(11)9.99-").Text),
        RwLine.PlusN(2, () => PoBatch.Line().Lit(1, "Goods are supplied on the terms and conditions")
            .Lit(64, "named above.  Quote the order number on the").Text),
        RwLine.Plus1(() => PoBatch.Line().Lit(1, "delivery note and the invoice.  An invoice that")
            .Lit(64, "does not match an approved order is held.").Text),
    ];
}
