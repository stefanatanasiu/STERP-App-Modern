using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Kit.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Kit.Application;

/// <summary>KITCOMMCA (cobol/copy/KITCOMMCA.cpy): the commercial kit request and the SETS answer.</summary>
internal sealed class KitCommArea : KitArea
{
    public string KitItem { get; set; } = "";
    public string KitVersion { get; set; } = "";
    public int KitDefId { get; set; }
    public string KvSite { get; set; } = "";
    public string OrderRef { get; set; } = "";
    public int OrderLine { get; set; }
    public decimal ParentQty { get; set; }
    public decimal ParentNet { get; set; }
    public string Currency { get; set; } = "";
    public string SupplyKind { get; set; } = "";
    public int ReturnLine { get; set; }
    public decimal ReturnQty { get; set; }
    public int AsAt { get; set; }

    // result
    public decimal CompleteSets { get; set; }
    public string LimitItem { get; set; } = "";
    public decimal LimitShort { get; set; }
    public decimal AllocTotal { get; set; }
    public decimal Residual { get; set; }
    public decimal CostTotal { get; set; }
    public decimal MarginGbp { get; set; }
    public decimal MarginPct { get; set; }
    public decimal CreditGbp { get; set; }
    public string Reconciled { get; set; } = "";
    public string MayDespatch { get; set; } = "";
    public string ParentStock { get; set; } = "";
    public int LineCnt { get; set; }
    public string NoteText { get; set; } = "";
    public KitCommLine[] Lines { get; } = Enumerable.Range(0, 15).Select(_ => new KitCommLine()).ToArray();
}

/// <summary>KV-LINES (one of 15) and the program's Ws-Ln table entry.</summary>
internal sealed class KitCommLine
{
    public int LineNum { get; set; }
    public string Item { get; set; } = "";
    public string Desc { get; set; } = "";
    public decimal QtyPer { get; set; }
    public string Uom { get; set; } = "";
    public decimal Required { get; set; }
    public decimal Free { get; set; }
    public decimal Delivered { get; set; }
    public decimal Sets { get; set; }
    public decimal Ssp { get; set; }
    public decimal AllocPct { get; set; }
    public decimal AllocNet { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Cost { get; set; }
    public string Vat { get; set; } = "";
    public string CustSpec { get; set; } = "";
    public string SubOk { get; set; } = "";
    public string Optional { get; set; } = "";
    public string Limiting { get; set; } = "";

    public KitCommLine Copy() => (KitCommLine)MemberwiseClone();
}

/// <summary>
/// KITCOMM (cobol/online/kit/KITCOMM.cob) - commercial kit fulfilment. Only SETS has a production caller
/// (frmKitMaint.SetsClicked); ALLOCATE, FULFIL and RETURNCK are not exposed (decision D-KIT-04) and are refused like any
/// other unknown function. Read only: SQLIO reads of the kit definition and AVAILQ AVAIL per component line.
/// </summary>
internal sealed class KitComm(ISqlRunner sql, IAvailabilityService availq)
{
    private const string DefinitionSelect =
        "SELECT TOP 1 CONVERT(varchar(12),d.KitDefId), d.KitVersion," +
        " d.SupplyKind, CONVERT(varchar(2),d.AllowPartialDespatch)," +
        " d.Status" +
        " FROM kit.KitDefinition d" +
        " WHERE d.CompanyCode = ? AND d.KitItemCode = ?" +
        " AND (? = '' OR d.KitVersion = ?)" +
        " AND d.Status = 'RELEASED'" +
        " AND d.EffectiveFrom <= CONVERT(date,?,112)" +
        " AND (d.EffectiveTo IS NULL" +
        "      OR d.EffectiveTo >= CONVERT(date,?,112))" +
        " ORDER BY d.EffectiveFrom DESC, d.KitDefId DESC";

    private const string LinesSelect =
        "SELECT CONVERT(varchar(12),l.LineNum), l.ComponentItemCode," +
        " ISNULL(i.Description,''), CONVERT(varchar(26),l.Quantity)," +
        " l.UomCode," +
        " CONVERT(varchar(26),ISNULL(l.StandaloneSellingPrice,0))," +
        " ISNULL(l.VatCode,'')," +
        " CONVERT(varchar(2),l.IsCustomerSpecified)," +
        " CONVERT(varchar(2),l.SubstitutionAllowed)," +
        " CONVERT(varchar(2),l.IsOptional)," +
        " CONVERT(varchar(26),ISNULL(c.StandardUnitCost,0))" +
        " FROM kit.KitDefinitionLine l" +
        " LEFT JOIN inv.Item i ON i.CompanyCode = ?" +
        "   AND i.ItemCode = l.ComponentItemCode" +
        " LEFT JOIN cst.ItemCost c ON c.CompanyCode = ?" +
        "   AND c.ItemCode = l.ComponentItemCode" +
        "   AND c.SiteCode = '*'" +
        " WHERE l.KitDefId = ? ORDER BY l.LineNum";

    private static readonly int[] Wide = [.. Enumerable.Repeat(100, 14)];

    private readonly List<KitCommLine> _ln = [];

    public async Task LinkAsync(KitCommArea ca, CancellationToken ct)
    {
        ca.Program = "KITCOMM";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "KIT-023";
        ClearAnswer(ca);
        switch (ca.Function)
        {
            case "SETS":
                await CountCompleteSetsAsync(ca, ct);
                break;
            default:
                ca.Set(8, "KIT0002", Pic.Msg("KITCOMM does not support the function ", Pic.Trim(ca.Function)));
                break;
        }
    }

    private static void SetSqlFailure(KitCommArea ca, SqlioException ex) => ca.Set(12, "SQL0001", Pic.X(ex.DriverText, 200));

    /// <summary>Clear-Answer: the parent of a commercial kit never has a stock balance (KIT-001).</summary>
    private void ClearAnswer(KitCommArea ca)
    {
        _ln.Clear();
        ca.CompleteSets = 0;
        ca.LimitItem = "";
        ca.LimitShort = 0;
        ca.AllocTotal = 0;
        ca.Residual = 0;
        ca.CostTotal = 0;
        ca.MarginGbp = 0;
        ca.MarginPct = 0;
        ca.CreditGbp = 0;
        ca.LineCnt = 0;
        ca.Reconciled = "N";
        ca.MayDespatch = "N";
        ca.ParentStock = "N";
        ca.NoteText = "";
        if (Pic.Blank(ca.Currency)) ca.Currency = "GBP";
        if (ca.AsAt == 0) ca.AsAt = ca.BusinessDate;
    }

    /// <summary>Load-Definition: the released definition for the version asked for, else the latest effective one.</summary>
    private async Task LoadDefinitionAsync(KitCommArea ca, CancellationToken ct)
    {
        if (Pic.Blank(ca.KitItem))
        {
            ca.Set(8, "KIT0001", "A commercial kit item code is required.");
            return;
        }
        var asAt = SqlioParm.Text(Pic.Trim(CobolNumeric.SqlioInteger(ca.AsAt)));
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(DefinitionSelect,
            [
                SqlioParm.Text(Pic.Pad(ca.Company, 10)), SqlioParm.Text(Pic.Pad(ca.KitItem, 40)),
                SqlioParm.Text(Pic.Pad(ca.KitVersion, 4)), SqlioParm.Text(Pic.Pad(ca.KitVersion, 4)), asAt, asAt,
            ], Wide, ct);
        }
        catch (SqlioException ex)
        {
            SetSqlFailure(ca, ex);
            return;
        }
        if (!row.Found)
        {
            ca.Set(16, "KIT0001", Pic.Msg("No released commercial kit definition for ", Pic.Trim(ca.KitItem), " is effective on this date."));
            return;
        }
        ca.KitDefId = (int)Pic.N(row.NumVal(0), 9, 0);
        ca.KitVersion = Pic.X(row.Raw(1), 4);
        ca.SupplyKind = Pic.X(row.Raw(2), 10);
    }

    /// <summary>Load-Lines: up to 15 component lines (further rows are fetched and dropped); none is KIT0020.</summary>
    private async Task LoadLinesAsync(KitCommArea ca, CancellationToken ct)
    {
        _ln.Clear();
        try
        {
            var rows = await sql.FetchAllAsync(LinesSelect,
            [
                SqlioParm.Text(Pic.Pad(ca.Company, 10)), SqlioParm.Text(Pic.Pad(ca.Company, 10)), SqlioParm.Integer(ca.KitDefId),
            ], Wide, ct);
            foreach (var row in rows) StoreLine(row);
        }
        catch (SqlioException ex)
        {
            SetSqlFailure(ca, ex);
        }
        ca.LineCnt = _ln.Count;
        if (_ln.Count == 0 && !ca.Failed)
            ca.Set(8, "KIT0020", "The kit definition has no component lines.");
    }

    private void StoreLine(SqlioRow row)
    {
        if (_ln.Count >= 15) return;
        _ln.Add(new KitCommLine
        {
            LineNum = (int)Pic.N(row.NumVal(0), 4, 0),
            Item = Pic.X(row.Raw(1), 40),
            Desc = Pic.X(row.Raw(2), 40),
            QtyPer = Pic.Q(row.NumVal(3)),
            Uom = Pic.X(row.Raw(4), 10),
            Ssp = Pic.N(row.NumVal(5), 11, 6),
            Vat = Pic.X(row.Raw(6), 4),
            CustSpec = row.Raw(7).StartsWith('1') ? "Y" : "N",
            SubOk = row.Raw(8).StartsWith('1') ? "Y" : "N",
            Optional = row.Raw(9).StartsWith('1') ? "Y" : "N",
            UnitCost = Pic.N(row.NumVal(10), 11, 6),
            Free = 0,
            Required = 0,
            Delivered = 0,
            Sets = 0,
            AllocPct = 0,
            AllocNet = 0,
            Cost = 0,
            Limiting = "N",
        });
    }

    /// <summary>
    /// SETS - Count-Complete-Sets: the minimum over the mandatory components of INTEGER(free / quantity per); an optional
    /// line cannot stop a set being complete.
    /// </summary>
    private async Task CountCompleteSetsAsync(KitCommArea ca, CancellationToken ct)
    {
        await LoadDefinitionAsync(ca, ct);
        if (ca.Failed) return;
        await LoadLinesAsync(ca, ct);
        if (ca.Failed) return;
        var sets = 0m;
        var first = true;
        var largest = 0;
        for (var w = 1; w <= _ln.Count; w++)
        {
            var ln = _ln[w - 1];
            await ReadFreeStockAsync(ca, ln, ct);
            ln.Required = Pic.Q(ln.QtyPer * ca.ParentQty);
            var lineSets = ln.QtyPer > 0 ? Pic.Q(decimal.Floor(ln.Free / ln.QtyPer)) : 0m;
            ln.Sets = lineSets;
            if (ln.Optional != "Y")
            {
                if (first)
                {
                    sets = lineSets;
                    first = false;
                    largest = w;
                }
                else if (lineSets < sets)
                {
                    sets = lineSets;
                    largest = w;
                }
            }
        }
        ca.CompleteSets = sets;
        if (largest > 0)
        {
            var ln = _ln[largest - 1];
            ln.Limiting = "Y";
            ca.LimitItem = ln.Item;
            var required = Pic.Q(ln.QtyPer * ca.ParentQty);
            var shortQty = Pic.Q(required - ln.Free);
            ca.LimitShort = shortQty < 0 ? 0 : shortQty;
        }
        DecideDespatch(ca);
        PublishLines(ca);
        NoteSets(ca);
    }

    /// <summary>Read-Free-Stock: AVAILQ AVAIL at the kit site and as-at date; any refusal counts as no free stock.</summary>
    private async Task ReadFreeStockAsync(KitCommArea ca, KitCommLine ln, CancellationToken ct)
    {
        var va = new AvailqCommarea
        {
            ItemCode = ln.Item,
            Site = ca.KvSite,
            AsAt = CobolDate.FromYyyymmdd(ca.AsAt),
        };
        var r = await availq.ExecuteAsync(AvailqFunctions.Avail, va,
            new ItmCall(ca.Company, ca.KvSite, ca.User, CobolDate.FromYyyymmdd(ca.BusinessDate), ""), ct);
        ln.Free = r.ReturnCode > 4 ? 0 : Pic.Q(va.QtyFree);
    }

    /// <summary>Decide-Despatch (KIT-023): no partial despatch of a commercial kit.</summary>
    private static void DecideDespatch(KitCommArea ca)
    {
        if (ca.CompleteSets >= ca.ParentQty && ca.ParentQty > 0)
        {
            ca.MayDespatch = "Y";
            return;
        }
        ca.MayDespatch = "N";
        if (ca.ParentQty > 0)
            ca.Set(4, "KIT0020", "An incomplete component set is not a kit and may not be despatched.");
    }

    private static string QtyEdit(decimal value) => Pic.Trim(CobolEdit.Format(value, "-(9)9.9(6)"));

    /// <summary>Note-Sets: "Free stock supports n complete sets[; limited by item, short s.]".</summary>
    private static void NoteSets(KitCommArea ca)
    {
        ca.NoteText = Pic.Str(200, "Free stock supports ", QtyEdit(ca.CompleteSets), " complete sets");
        if (!Pic.Blank(ca.LimitItem))
            ca.NoteText = Pic.Str(200, Pic.Trim(ca.NoteText), "; limited by ", Pic.Trim(ca.LimitItem), ", short ", QtyEdit(ca.LimitShort), ".");
    }

    private void PublishLines(KitCommArea ca)
    {
        ca.LineCnt = _ln.Count;
        for (var i = 0; i < _ln.Count; i++)
        {
            var src = _ln[i];
            var dst = ca.Lines[i];
            dst.LineNum = src.LineNum;
            dst.Item = src.Item;
            dst.Desc = src.Desc;
            dst.QtyPer = src.QtyPer;
            dst.Uom = src.Uom;
            dst.Required = src.Required;
            dst.Free = src.Free;
            dst.Delivered = src.Delivered;
            dst.Sets = src.Sets;
            dst.Ssp = src.Ssp;
            dst.AllocPct = src.AllocPct;
            dst.AllocNet = src.AllocNet;
            dst.UnitCost = src.UnitCost;
            dst.Cost = src.Cost;
            dst.Vat = src.Vat;
            dst.CustSpec = src.CustSpec;
            dst.SubOk = src.SubOk;
            dst.Limiting = src.Limiting;
        }
    }
}

/// <summary>KitExecutionService.Commercial (KitExecutionService.vb:157-179) for the SETS function the maintenance page sends.</summary>
internal sealed class KitCommService(KitComm kitComm)
{
    public async Task<(LegacyResult Result, KitCommArea Area, List<KitCommLine> Lines)> SetsAsync(KitSession s, string kitItem, string version,
        string orderRef, int orderLine, decimal parentQty, CancellationToken ct)
    {
        var ca = new KitCommArea
        {
            KitItem = Pic.X(kitItem, 40),
            KitVersion = Pic.X(version, 4),
            KvSite = Pic.X(s.Site, 10),
            OrderRef = Pic.X(orderRef, 40),
            OrderLine = orderLine,
            ParentQty = Pic.Q(parentQty),
            ParentNet = 0,
            Currency = "GBP",
            ReturnLine = 0,
            ReturnQty = 0,
            AsAt = s.BusinessDate,
        };
        ca.Prepare(s, "SETS");
        await kitComm.LinkAsync(ca, ct);
        var r = ca.Result;
        var lines = r.Succeeded ? ca.Lines.Take(Math.Clamp(ca.LineCnt, 0, 15)).Select(l => l.Copy()).ToList() : [];
        return (r, ca, lines);
    }
}
