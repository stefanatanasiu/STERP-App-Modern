using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.So.Domain;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Endpoints;

/// <summary>
/// The COMMAREA-backed online operations: each handler builds the program input exactly as the SalesOrderService member did
/// (dotnet/STERP.Business/Sales/SalesOrderService.vb:106-369), fills the STCAHDR header as CobolGateway.Link did, applies the
/// contracts 4.4 input normalization, runs the re-implemented program and returns the outcome envelope (or problem) with the
/// COMMAREA body as data (D-SO-03).
/// </summary>
public static class ProgramEndpoints
{
    public static void Map(OperationRegistry ops)
    {
        ops.Handle("so_explainPrice", ExplainPriceAsync);
        ops.Handle("so_calculateDocumentTotals", CalculateDocumentTotalsAsync);
        ops.Handle("so_decideLineVat", DecideLineVatAsync);
        ops.Handle("so_measureLineMargin", MeasureLineMarginAsync);
        ops.Handle("so_enquireAvailability", EnquireAvailabilityAsync);
        ops.Handle("so_reserveSalesOrderLine", ReserveSalesOrderLineAsync);
        ops.Handle("so_validateSalesOrder", ValidateSalesOrderAsync);
        ops.Handle("so_acceptSalesOrder", AcceptSalesOrderAsync);
        ops.Handle("so_confirmDespatch", ConfirmDespatchAsync);
    }

    /// <summary>The company base currency (SterpSession.BaseCurrency) the facade used for a blank currency.</summary>
    private static string BaseCurrency(OperationContext ctx) => ctx.Session.CompanyAccess?.BaseCurrency ?? "";

    private static string CurrencyOrBase(OperationContext ctx, string currency) => currency == "" ? BaseCurrency(ctx) : currency;

    private static IResult Answer(OperationContext ctx, Commarea area) => ctx.Envelope(area.Outcome(), SoCodec.ToJson(area));

    /// <summary>so_explainPrice: SalesOrderService.Price("EXPLAIN") (SalesOrderService.vb:106-121) -> PRICING EXPLAIN.</summary>
    public static async Task<IResult> ExplainPriceAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "PRICING";
        var b = await ctx.BodyObjectAsync();
        var pr = new PricingArea();
        Header.Link(pr, ctx.Session, "PRICING", "EXPLAIN");
        var date = CobolDate.ToYyyymmdd(Req.Date(b, "orderDate") ?? ctx.Session.BusinessDate);
        pr.ItemCode = Req.S(b, "itemCode");
        pr.Customer = Req.S(b, "customer");
        pr.UomCode = Req.S(b, "uomCode");
        pr.Currency = CurrencyOrBase(ctx, Req.S(b, "currency"));
        pr.Quantity = Req.D(b, "quantity");
        pr.OrderDate = date;
        pr.PriceDate = date;
        pr.DocType = "SALESORDER";
        pr.DocRef = Req.S(b, "docRef");
        SoCodec.Normalize(pr);
        await ctx.Get<PricingProgram>().RunAsync(pr, ctx.Aborted);
        return Answer(ctx, pr);
    }

    /// <summary>The facade line cap of SODISC, DESPATCH and PICKGEN (SalesOrderService.MaxLines).</summary>
    public const int MaxLines = 30;

    /// <summary>
    /// so_confirmDespatch: SalesOrderService.NewDespatch + AddDespatchLine + Despatch("CONFIRM") (SalesOrderService.vb:316-369) -
    /// DS-SITE = session site, DS-POSTING-DATE = business date, the Idempotency-Key as the correlation id - then DESPATCH CONFIRM.
    /// </summary>
    public static async Task<IResult> ConfirmDespatchAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "DESPATCH";
        var b = await ctx.BodyObjectAsync();
        var ds = new DespatchArea();
        Header.Link(ds, ctx.Session, "DESPATCH", "CONFIRM");
        ds.OrderNumber = ctx.Route("orderNumber");
        ds.PicklistRef = Req.S(b, "picklistRef");
        ds.Customer = Req.S(b, "customer");
        ds.Site = ctx.Session.SiteCode;
        ds.DespatchDate = CobolDate.ToYyyymmdd(Req.Date(b, "despatchDate"));
        ds.PostingDate = CobolDate.ToYyyymmdd(ctx.Session.BusinessDate);
        ds.CarrierCode = Req.S(b, "carrierCode");
        ds.CarrierRef = Req.S(b, "carrierRef");
        ds.PackCount = Req.I(b, "packCount");
        ds.GrossWeight = Req.D(b, "grossWeight");
        ds.Incoterms = Req.S(b, "incoterms");
        ds.IncotermsPlace = Req.S(b, "incotermsPlace");
        ds.IsExport = Req.S(b, "isExport") is "Y" or "true" ? "Y" : "N";
        ds.DestCountry = Req.S(b, "destCountry");
        ds.CustomerEori = Req.S(b, "customerEori");
        ds.ExportDeclRef = Req.S(b, "exportDeclRef");
        ds.LineCnt = 0;
        var index = 0;
        foreach (var node in Req.Array(b, "lines") ?? [])
        {
            index++;
            if (index > ds.Lines.Length)
            {
                ds.LineCnt = index;
                continue;
            }
            var r = node as JsonObject ?? [];
            var l = ds.Lines[index - 1];
            l.LLineNum = index;
            l.LSchedule = Req.I(r, "schedule");
            l.LWhse = Req.S(r, "warehouse");
            l.LLocation = Req.S(r, "location");
            l.LOrderLine = Req.I(r, "orderLine");
            l.LItem = Req.S(r, "itemCode");
            l.LDesc = Req.S(r, "description");
            l.LPicked = Req.D(r, "pickedQty");
            l.LQty = Req.D(r, "quantity");
            l.LUom = Req.S(r, "uom");
            l.LLot = Req.S(r, "lot");
            l.LSerial = Req.S(r, "serial");
            ds.LineCnt = index;
        }
        SoCodec.Normalize(ds);
        await ctx.Get<DespatchProgram>().RunAsync(ds, ctx.Aborted);
        return Answer(ctx, ds);
    }

    /// <summary>so_validateSalesOrder: SalesOrderService.Accept("VALIDATE") (SalesOrderService.vb:224-243) -> SOACCEPT VALIDATE.</summary>
    public static Task<IResult> ValidateSalesOrderAsync(OperationContext ctx) => AcceptanceAsync(ctx, "VALIDATE");

    /// <summary>
    /// so_acceptSalesOrder: SalesOrderService.Accept("ACCEPT") with a new correlation id (the Idempotency-Key) -> SOACCEPT ACCEPT;
    /// a replay re-runs (SOACCEPT uses no IDEMPOT).
    /// </summary>
    public static Task<IResult> AcceptSalesOrderAsync(OperationContext ctx) => AcceptanceAsync(ctx, "ACCEPT");

    private static async Task<IResult> AcceptanceAsync(OperationContext ctx, string function)
    {
        ctx.Session.LegacyProgram = "SOACCEPT";
        var b = await ctx.BodyObjectAsync();
        var sa = new SoacceptArea();
        Header.Link(sa, ctx.Session, "SOACCEPT", function);
        sa.OrderNumber = ctx.Route("orderNumber");
        sa.Customer = Req.S(b, "customer");
        sa.OrderType = "STANDARD";
        sa.OrderDate = CobolDate.ToYyyymmdd(Req.Date(b, "orderDate") ?? ctx.Session.BusinessDate);
        sa.Currency = CurrencyOrBase(ctx, Req.S(b, "currency"));
        sa.TermsCode = Req.S(b, "termsCode");
        sa.Salesperson = Req.S(b, "salesperson");
        sa.IncotermsRule = Req.S(b, "incotermsRule");
        sa.IncotermsPlace = Req.S(b, "incotermsPlace");
        sa.DeliveryRef = Req.S(b, "deliveryRef");
        sa.PrintOnly = "N";
        SoCodec.Normalize(sa);
        await ctx.Get<SoacceptProgram>().RunAsync(sa, ctx.Aborted);
        return Answer(ctx, sa);
    }

    /// <summary>
    /// so_enquireAvailability: SalesOrderService.Availability (SalesOrderService.vb:252-268) - site blank -> session site,
    /// SA-AS-AT = business date, horizon 90, receipts Y, kit N - then SOAVAIL ENQUIRE.
    /// </summary>
    public static async Task<IResult> EnquireAvailabilityAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "SOAVAIL";
        var b = await ctx.BodyObjectAsync();
        var sa = new SoavailArea();
        Header.Link(sa, ctx.Session, "SOAVAIL", "ENQUIRE");
        var site = Req.S(b, "site");
        sa.ItemCode = Req.S(b, "itemCode");
        sa.Site = site == "" ? ctx.Session.SiteCode : site;
        sa.UomCode = Req.S(b, "uomCode");
        sa.QtyRequired = Req.D(b, "qtyRequired");
        sa.RequiredDate = CobolDate.ToYyyymmdd(Req.Date(b, "requiredDate") ?? ctx.Session.BusinessDate);
        sa.AsAt = CobolDate.ToYyyymmdd(ctx.Session.BusinessDate);
        sa.HorizonDays = 90;
        sa.WithReceipts = "Y";
        sa.WithKit = "N";
        SoCodec.Normalize(sa);
        await ctx.Get<SoavailProgram>().RunAsync(sa, ctx.Aborted);
        return Answer(ctx, sa);
    }

    /// <summary>
    /// so_reserveSalesOrderLine: SalesOrderService.Reserve (SalesOrderService.vb:277-293) - demand SALESORDER order/line,
    /// partial allowed, a new correlation id (the Idempotency-Key) - then SORESV RESERVE.
    /// </summary>
    public static async Task<IResult> ReserveSalesOrderLineAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "SORESV";
        var b = await ctx.BodyObjectAsync();
        var order = ctx.Route("orderNumber");
        var line = int.TryParse(ctx.Route("lineNum"), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : throw ApiProblemException.BadBody("lineNum must be an integer.");
        var sv = new SoresvArea();
        Header.Link(sv, ctx.Session, "SORESV", "RESERVE");
        var site = Req.S(b, "site");
        sv.OrderNumber = order;
        sv.LineNum = line;
        sv.ItemCode = Req.S(b, "itemCode");
        sv.Site = site == "" ? ctx.Session.SiteCode : site;
        sv.Customer = Req.S(b, "customer");
        sv.DemandType = "SALESORDER";
        sv.DemandRef = order;
        sv.DemandLine = line;
        sv.QtyRequested = Req.D(b, "qtyRequested");
        sv.RequiredDate = CobolDate.ToYyyymmdd(Req.Date(b, "requiredDate") ?? ctx.Session.BusinessDate);
        sv.AllowPartial = "Y";
        SoCodec.Normalize(sv);
        await ctx.Get<SoresvProgram>().RunAsync(sv, ctx.Aborted);
        return Answer(ctx, sv);
    }

    /// <summary>
    /// so_decideLineVat: SalesOrderService.Vat("DECIDE") (SalesOrderService.vb:177-196) - SV-LINE-TYPE GOODS, SV-READ-MASTER Y,
    /// every location/evidence/override input left at its initial value - then SOVAT DECIDE.
    /// </summary>
    public static async Task<IResult> DecideLineVatAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "SOVAT";
        var b = await ctx.BodyObjectAsync();
        var sv = new SovatArea();
        Header.Link(sv, ctx.Session, "SOVAT", "DECIDE");
        sv.DocType = "SALESORDER";
        sv.DocRef = Req.S(b, "docRef");
        sv.LineNum = Req.I(b, "lineNum");
        sv.ItemCode = Req.S(b, "itemCode");
        sv.LineType = "GOODS";
        sv.Customer = Req.S(b, "customer");
        sv.ItemVatCode = Req.S(b, "itemVatCode");
        sv.NetAmount = Req.D(b, "netAmount");
        sv.Currency = CurrencyOrBase(ctx, Req.S(b, "currency"));
        sv.SupplyDate = CobolDate.ToYyyymmdd(Req.Date(b, "supplyDate") ?? ctx.Session.BusinessDate);
        sv.ReadMaster = "Y";
        SoCodec.Normalize(sv);
        await ctx.Get<SovatProgram>().RunAsync(sv, ctx.Aborted);
        return Answer(ctx, sv);
    }

    /// <summary>
    /// so_measureLineMargin: SalesOrderService.Margin("LINE") (SalesOrderService.vb:198-218) - SM-SITE-CODE = the session site,
    /// SM-COST-BASIS STANDARD - then SOMARGN LINE.
    /// </summary>
    public static async Task<IResult> MeasureLineMarginAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "SOMARGN";
        var b = await ctx.BodyObjectAsync();
        var sm = new SomargnArea();
        Header.Link(sm, ctx.Session, "SOMARGN", "LINE");
        sm.DocType = "SALESORDER";
        sm.DocRef = Req.S(b, "docRef");
        sm.LineNum = Req.I(b, "lineNum");
        sm.ItemCode = Req.S(b, "itemCode");
        sm.Customer = Req.S(b, "customer");
        sm.SiteCode = ctx.Session.SiteCode;
        sm.EffDate = CobolDate.ToYyyymmdd(Req.Date(b, "effDate") ?? ctx.Session.BusinessDate);
        sm.Quantity = Req.D(b, "quantity");
        sm.NetAmount = Req.D(b, "netAmount");
        sm.Currency = CurrencyOrBase(ctx, Req.S(b, "currency"));
        sm.CostBasis = "STANDARD";
        SoCodec.Normalize(sm);
        await ctx.Get<SomargnProgram>().RunAsync(sm, ctx.Aborted);
        return Answer(ctx, sm);
    }

    /// <summary>
    /// so_calculateDocumentTotals: SalesOrderService.Discount("DOCUMENT") (SalesOrderService.vb:134-175) - at most the first
    /// 30 lines mapped by AddDiscountLine, SD-ROUND-PLACES 2 - then SODISC DOCUMENT.
    /// </summary>
    public static async Task<IResult> CalculateDocumentTotalsAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "SODISC";
        var b = await ctx.BodyObjectAsync();
        var sd = new SodiscArea();
        Header.Link(sd, ctx.Session, "SODISC", "DOCUMENT");
        sd.DocType = "SALESORDER";
        sd.DocRef = Req.S(b, "docRef");
        sd.Customer = Req.S(b, "customer");
        sd.Currency = CurrencyOrBase(ctx, Req.S(b, "currency"));
        sd.PriceDate = CobolDate.ToYyyymmdd(Req.Date(b, "priceDate") ?? ctx.Session.BusinessDate);
        sd.RoundPlaces = 2;
        var n = 0;
        foreach (var node in Req.Array(b, "lines") ?? [])
        {
            if (n >= MaxLines) break;
            n++;
            AddDiscountLine(sd.Line[n - 1], n, node as JsonObject ?? []);
        }
        sd.LineCnt = n;
        SoCodec.Normalize(sd);
        await ctx.Get<SodiscProgram>().RunAsync(sd, ctx.Aborted);
        return Answer(ctx, sd);
    }

    /// <summary>SalesOrderService.AddDiscountLine (SalesOrderService.vb:157-175): Col(r, name, default) = absent or null gives the default.</summary>
    private static void AddDiscountLine(SodiscLine l, int index, JsonObject r)
    {
        var per = Req.Has(r, "pricePerQty") ? Req.D(r, "pricePerQty") : 1m;
        if (per == 0m) per = 1m;
        l.LLineNum = Req.Has(r, "lineNum") ? Req.I(r, "lineNum") : index;
        l.LItemCode = Req.S(r, "itemCode");
        l.LEntQty = Req.D(r, "acceptedQty");
        l.LEntUom = Req.S(r, "uomCode");
        l.LPriceUom = Req.Has(r, "priceUomCode") ? Req.S(r, "priceUomCode") : Req.S(r, "uomCode");
        l.LFactor = Req.D(r, "conversionFactor");
        l.LUnitPrice = Req.D(r, "unitPrice");
        l.LPerQty = per;
        l.LDiscPct1 = Req.D(r, "discountPct1");
        l.LDiscPct2 = Req.D(r, "discountPct2");
        l.LVatCode = Req.S(r, "vatCode");
        l.LVatRate = Req.D(r, "vatRatePct");
        l.LCostGbp = Req.D(r, "costSnapshotGbp");
    }
}
