using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Po.Endpoints;

/// <summary>
/// GoodsReceiptService (dotnet/STERP.Business/Purchasing/GoodsReceiptService.vb): validate (outstanding-lines query +
/// RECEIPT VALIDATE), confirm (RECEIPT CONFIRM over the validated document the page sends back, D-PO-03), reverse
/// (stored-lines query + RECEIPT REVERSE) and record inspection (INSPECT RECORD). FR-STERP-PO-031..034.
/// </summary>
internal static class GoodsReceiptEndpoints
{
    /// <summary>GoodsReceiptService.OutstandingLines (GoodsReceiptService.vb:21-44), the only read of the goods-inward path.</summary>
    private const string OutstandingLines =
        "SELECT TOP 20 l.LineNum, l.ItemCode, ISNULL(l.Revision,'') AS Revision, " +
        "ISNULL(l.Description,'') AS Description, l.OrderedQty, l.CancelledQty, l.ReceivedQty, " +
        "o.SupplierCode, o.CurrencyCode, o.FxRate, ISNULL(o.IncotermsRule,'') AS IncotermsRule, " +
        "l.UomCode, l.ConversionFactor, l.UnitPrice, " +
        "ISNULL(l.OverDeliveryTolerancePct,0) AS OverDeliveryTolerancePct, " +
        "ScheduleNo = ISNULL((SELECT TOP 1 s.ScheduleNo FROM po.PurchaseSchedule s " +
        "WHERE s.PurchaseOrderLineId = l.PurchaseOrderLineId AND s.Status IN ('OPEN','PARTIAL') " +
        "ORDER BY ISNULL(s.CurrentConfirmedDate, s.RequestedDate), s.ScheduleNo), 1), " +
        "Promised = (SELECT TOP 1 ISNULL(s.CurrentConfirmedDate, s.RequestedDate) FROM po.PurchaseSchedule s " +
        "WHERE s.PurchaseOrderLineId = l.PurchaseOrderLineId AND s.Status IN ('OPEN','PARTIAL') " +
        "ORDER BY ISNULL(s.CurrentConfirmedDate, s.RequestedDate), s.ScheduleNo), " +
        "Traceability = ISNULL(i.TraceabilityType,'NONE'), " +
        "NeedsInspection = ISNULL(i.RequiresInspection,0), " +
        "IsStocked = CASE WHEN l.LineType IN ('SERVICE','CHARGE','TEXT') THEN 0 ELSE ISNULL(i.IsStocked,1) END, " +
        "DefaultLocation = COALESCE(NULLIF(l.DestinationLocationCode, ''), " +
        "(SELECT TOP 1 loc.LocationCode FROM inv.Location loc WHERE loc.CompanyCode = l.CompanyCode " +
        "AND loc.SiteCode = @s AND loc.IsActive = 1 AND loc.LocationType IN ('GOODSIN','STOCK') " +
        "ORDER BY CASE WHEN loc.LocationType = 'GOODSIN' THEN 0 ELSE 1 END, loc.LocationCode), '') " +
        "FROM po.PurchaseOrderLine l " +
        "JOIN po.PurchaseOrder o ON o.PurchaseOrderId = l.PurchaseOrderId AND o.SiteCode = @s " +
        "LEFT JOIN inv.Item i ON i.CompanyCode = l.CompanyCode AND i.ItemCode = l.ItemCode " +
        "WHERE l.CompanyCode = @c AND l.OrderNumber = @o AND l.Status NOT IN ('CANCELLED','SHORTCLOSED') " +
        "AND l.OrderedQty - l.CancelledQty - l.ReceivedQty > 0 ORDER BY l.LineNum";

    /// <summary>The stored lines of a confirmed receipt (GoodsReceiptService.vb:190-197).</summary>
    private const string StoredLines =
        "SELECT r.SupplierCode, r.PurchaseOrderNumber, r.WarehouseCode, r.OwnershipBasis, " +
        "l.LineNum, l.PurchaseOrderLine, l.ScheduleNo, l.ItemCode, l.Revision, l.ReceivedQty, " +
        "l.StockQty, l.AcceptedQty, l.QuarantineQty, l.UomCode, l.ConversionFactor, " +
        "l.LocationCode, l.LotCode, l.SerialNo, l.UnitPrice, l.MovementId " +
        "FROM po.Receipt r JOIN po.ReceiptLine l ON l.ReceiptId = r.ReceiptId " +
        "WHERE r.CompanyCode = @c AND r.SiteCode = @s AND r.ReceiptRef = @r " +
        "AND r.Status = 'CONFIRMED' ORDER BY l.LineNum";

    public static void Map(OperationRegistry ops)
    {
        ops.Handle("po_validateGoodsReceipt", ValidateAsync);
        ops.Handle("po_confirmGoodsReceipt", ConfirmAsync);
        ops.Handle("po_reverseGoodsReceipt", ReverseAsync);
        ops.Handle("po_recordInspection", InspectAsync);
    }

    /// <summary>po_validateGoodsReceipt: GoodsReceiptService.Validate + LoadLines (GoodsReceiptService.vb:60-146); rule R4.</summary>
    private static async Task<IResult> ValidateAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "RECEIPT";
        var v = await ctx.BodyObjectAsync();
        var ownership = PoMap.S(v, "ownership");
        if (ownership.Length == 0) ownership = "RECEIPT";
        if (ownership is not ("RECEIPT" or "INCOTERMS" or "DROPSHIP"))
        {
            throw new ArgumentException("Ownership must be RECEIPT, INCOTERMS or DROPSHIP.", "v");
        }
        var business = PoMap.Business(ctx);
        var dup = PoMap.S(v, "dupPolicy");
        var rc = new ReceiptArea
        {
            OrderNumber = PoMap.S(v, "orderNumber"),
            Supplier = PoMap.S(v, "supplier"),
            DeliveryNote = PoMap.T(v, "deliveryNote"),
            PhysicalDate = PoMap.Dt(v, "physicalDate", business),
            PostingDate = PoMap.Dt(v, "postingDate", business),
            Warehouse = PoMap.S(v, "warehouse"),
            Ownership = ownership,
            Incoterm = PoMap.S(v, "incoterm"),
            ShipEvidence = PoMap.T(v, "shipEvidence"),
            ImportEntry = PoMap.S(v, "importEntry"),
            SubcontractJob = PoMap.S(v, "subcontractJob"),
            DupPolicy = dup.Length == 0 ? "WARN" : dup,
            ReceivedBy = ctx.Session.UserCode,
        };
        await LoadLinesAsync(ctx, rc, v);
        rc.Normalize();
        var ca = CaHeader.From(ctx.Session, "VALIDATE");
        await ctx.Get<ReceiptProgram>().RunAsync(ca, rc, ctx.Aborted);
        return PoMap.Answer(ctx, ca, rc.ToJson(), dataOnProblem: true);
    }

    /// <summary>LoadLines: the outstanding lines, this delivery defaulting to the whole outstanding quantity.</summary>
    private static async Task LoadLinesAsync(OperationContext ctx, ReceiptArea rc, JsonObject v)
    {
        var rows = await ctx.Get<IProcedureRunner>().WithReaderAsync(OutstandingLines, new
        {
            c = ctx.Session.CompanyCode,
            o = PoMap.S(v, "orderNumber"),
            s = ctx.Session.SiteCode,
        }, ReadRowsAsync, ctx.Aborted);
        var n = Math.Min(rows.Count, 20);
        rc.LineCnt = n;
        if (n > 0)
        {
            rc.Supplier = Str(rows[0]["SupplierCode"]);
            rc.Currency = Str(rows[0]["CurrencyCode"]);
            rc.FxRate = Dec(rows[0]["FxRate"]);
            rc.Incoterm = Str(rows[0]["IncotermsRule"]);
        }
        var loc = PoMap.S(v, "location");
        var lot = PoMap.S(v, "lotCode");
        var serial = PoMap.S(v, "serialNo");
        for (var i = 0; i < n; i++)
        {
            var row = rows[i];
            var l = rc.Lines[i];
            var outstanding = Dec(row["OrderedQty"]) - Dec(row["CancelledQty"]) - Dec(row["ReceivedQty"]);
            l.LNum = i + 1;
            l.LPoLine = Int(row["LineNum"]);
            l.LSchedule = Int(row["ScheduleNo"]);
            l.LItem = Str(row["ItemCode"]);
            l.LRevision = Str(row["Revision"]);
            l.LDesc = Str(row["Description"]);
            l.LOrdered = Dec(row["OrderedQty"]);
            l.LCancelled = Dec(row["CancelledQty"]);
            l.LCounted = Dec(row["ReceivedQty"]);
            l.LReceived = outstanding;
            l.LUom = Str(row["UomCode"]);
            l.LFactor = Dec(row["ConversionFactor"]) == 0m ? 1m : Dec(row["ConversionFactor"]);
            l.LPrice = Dec(row["UnitPrice"]);
            l.LOverPct = Dec(row["OverDeliveryTolerancePct"]);
            l.LTrace = Str(row["Traceability"]);
            l.LInspect = Bool(row["NeedsInspection"]) ? "Y" : "N";
            l.LStocked = Bool(row["IsStocked"]) ? "Y" : "N";
            l.LLocation = loc.Length == 0 ? Str(row["DefaultLocation"]) : loc;
            if (lot.Length > 0) l.LLot = lot;
            if (serial.Length > 0) l.LSerial = serial;
            if (row["Promised"] is DateTime promised) l.LPromised = CobolDate.ToYyyymmdd(promised);
        }
    }

    /// <summary>po_confirmGoodsReceipt: GoodsReceiptService.Confirm("CONFIRM") (GoodsReceiptService.vb:173-185); rule R4.</summary>
    private static async Task<IResult> ConfirmAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "RECEIPT";
        var v = await ctx.BodyObjectAsync();
        if (CaJson.Find(v, "receipt") is not JsonObject doc)
        {
            return ctx.Envelope(new Sterp.Platform.Kernel.LegacyResult(8, "RCP0000", "The delivery has not been validated against an order yet."));
        }
        var rc = ReceiptArea.FromJson(doc);
        rc.Normalize();
        var ca = CaHeader.From(ctx.Session, "CONFIRM");
        await ctx.Get<ReceiptProgram>().RunAsync(ca, rc, ctx.Aborted);
        return PoMap.Answer(ctx, ca, rc.ToJson(), dataOnProblem: true);
    }

    /// <summary>po_reverseGoodsReceipt: GoodsReceiptService.Reverse (GoodsReceiptService.vb:188-238).</summary>
    private static async Task<IResult> ReverseAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "RECEIPT";
        var v = await ctx.BodyObjectAsync();
        var receiptRef = ctx.Route("receiptRef");
        var rows = await ctx.Get<IProcedureRunner>().WithReaderAsync(StoredLines, new
        {
            c = ctx.Session.CompanyCode,
            s = ctx.Session.SiteCode,
            r = receiptRef,
        }, ReadRowsAsync, ctx.Aborted);
        if (rows.Count > 20) throw new InvalidOperationException("The receipt exceeds the engine's 20-line reversal limit.");
        var rc = new ReceiptArea
        {
            ReceiptRef = receiptRef,
            ReverseRef = receiptRef,
            ReverseReason = PoMap.T(v, "reverseReason"),
            ReceivedBy = ctx.Session.UserCode,
            PostingDate = PoMap.Business(ctx),
            LineCnt = rows.Count,
        };
        if (rows.Count > 0)
        {
            rc.Supplier = Str(rows[0]["SupplierCode"]);
            rc.OrderNumber = Str(rows[0]["PurchaseOrderNumber"]);
            rc.Warehouse = Str(rows[0]["WarehouseCode"]);
            rc.Ownership = Str(rows[0]["OwnershipBasis"]);
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var l = rc.Lines[i];
            l.LNum = Int(row["LineNum"]);
            l.LPoLine = Int(row["PurchaseOrderLine"]);
            l.LSchedule = Int(row["ScheduleNo"]);
            l.LItem = Str(row["ItemCode"]);
            l.LRevision = Str(row["Revision"]);
            l.LUom = Str(row["UomCode"]);
            l.LLocation = Str(row["LocationCode"]);
            l.LLot = Str(row["LotCode"]);
            l.LSerial = Str(row["SerialNo"]);
            l.LReceived = Dec(row["ReceivedQty"]);
            l.LStockQty = Dec(row["StockQty"]);
            l.LAccepted = Dec(row["AcceptedQty"]);
            l.LQuarant = Dec(row["QuarantineQty"]);
            l.LFactor = Dec(row["ConversionFactor"]);
            l.LPrice = Dec(row["UnitPrice"]);
            l.LStocked = Int(row["MovementId"]) > 0 ? "Y" : "N";
        }
        rc.Normalize();
        var ca = CaHeader.From(ctx.Session, "REVERSE");
        await ctx.Get<ReceiptProgram>().RunAsync(ca, rc, ctx.Aborted);
        return PoMap.Answer(ctx, ca, rc.ToJson());
    }

    /// <summary>po_recordInspection: GoodsReceiptService.Inspect (GoodsReceiptService.vb:241-268) -> INSPECT RECORD.</summary>
    private static async Task<IResult> InspectAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "INSPECT";
        var v = await ctx.BodyObjectAsync();
        var line = ctx.Route("lineNum");
        var ins = new InspectArea
        {
            ReceiptRef = ctx.Route("receiptRef").Trim().ToUpperInvariant(),
            ReceiptLine = (int)decimal.Truncate(decimal.TryParse(line, NumberStyles.Number, CultureInfo.InvariantCulture, out var ln) ? ln : 0m),
            ItemCode = PoMap.S(v, "itemCode"),
            LotCode = PoMap.S(v, "lotCode"),
            SerialNo = PoMap.S(v, "serialNo"),
            Warehouse = PoMap.S(v, "warehouse"),
            Location = PoMap.S(v, "location"),
            GoodLocation = PoMap.S(v, "goodLocation"),
            UomCode = PoMap.S(v, "uomCode"),
            Inspector = ctx.Session.UserCode,
            InspectDate = PoMap.Business(ctx),
            InspectedQty = PoMap.D(v, "inspectedQty"),
            AcceptedQty = PoMap.D(v, "acceptedQty"),
            RejectedQty = PoMap.D(v, "rejectedQty"),
            ReworkQty = PoMap.D(v, "reworkQty"),
            Disposition = PoMap.S(v, "disposition"),
            RejectReason = PoMap.S(v, "rejectReason"),
            RejectNarr = PoMap.T(v, "rejectNarr"),
            ConcessionRef = PoMap.T(v, "concessionRef"),
            ConcessionBy = PoMap.S(v, "concessionBy"),
            CertRef = PoMap.T(v, "certRef"),
        };
        ins.Normalize();
        var ca = CaHeader.From(ctx.Session, "RECORD");
        await ctx.Get<InspectProgram>().RunAsync(ca, ins, ctx.Aborted);
        return PoMap.Answer(ctx, ca, ins.ToJson());
    }

    private static async Task<List<Dictionary<string, object?>>> ReadRowsAsync(DbDataReader r)
    {
        var rows = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Fmt.Str: the value right-trimmed, "" for NULL.</summary>
    private static string Str(object? v) => v is null ? "" : (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").TrimEnd();

    /// <summary>Fmt.Dec.</summary>
    private static decimal Dec(object? v) => v switch
    {
        null => 0m,
        decimal d => d,
        _ => decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.GetCultureInfo("en-GB"), out var d) ? d : 0m,
    };

    /// <summary>Fmt.Int (int.TryParse of the text; 0 when it does not fit).</summary>
    private static int Int(object? v) =>
        v is not null && int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.GetCultureInfo("en-GB"), out var n) ? n : 0;

    /// <summary>Fmt.Bool.</summary>
    private static bool Bool(object? v) => v switch
    {
        null => false,
        bool b => b,
        _ => (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Trim().ToUpperInvariant() is "1" or "Y" or "YES" or "TRUE",
    };
}
