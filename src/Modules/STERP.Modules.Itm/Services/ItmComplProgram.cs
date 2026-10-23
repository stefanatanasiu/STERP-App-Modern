using Sterp.Modules.Itm.Data;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Itm.Services;

/// <summary>ITMCOMPLCA body (cobol/copy/ITMCOMPLCA.cpy, prefix CO-): text right-trimmed with the PIC X(n) widths applied.</summary>
internal sealed class ItmcomplArea
{
    public string ItemCode = "";
    public string Direction = "";
    public int TaxPoint;
    public string VatCode = "";
    public decimal VatRate;
    public string VatEvidence = "";
    public int VatFrom;
    public int VatTo;
    public string DrcGoods = "";
    public string DrcWording = "";
    public string CommodityCode = "";
    public string OriginCountry = "";
    public string Preferential = "";
    public string PrefEvidence = "";
    public decimal DutyRate;
    public string ExportControl = "";
    public string ExportLicence = "";
    public string SuppUnit = "";
    public decimal NetWeightKg;
    public int CustomsFrom;
    public string UkcaMarked = "";
    public string ConformityRef = "";
    public string DgUnNumber = "";
    public string DgClass = "";
    public string DgPackGroup = "";
    public string ReachStatus = "";
    public string WeeeCategory = "";
    public string BatteryCategory = "";
    public decimal DespatchQty;
    public decimal PkgTotalG;
    public decimal PkgPlasticG;
    public decimal PkgRecycledG;
    public string PptLiable = "";
    public string Valid = "";
    public int FaultCnt;
    public int LineCnt;
    public readonly ItmcomplLine[] Lines = Enumerable.Range(0, 12).Select(_ => new ItmcomplLine()).ToArray();
}

/// <summary>CO-LINES (OCCURS 12).</summary>
internal sealed class ItmcomplLine
{
    public string Material = "";
    public string Type = "";
    public decimal WeightG;
    public decimal Recycled;
    public decimal TotalG;
    public decimal RecycG;
    public string Ppt = "";
}

/// <summary>
/// ITMCOMPL (cobol/online/itm/ITMCOMPL.cob; FR-STERP-ITM-027), internal to PKGRPT: VATCODE, CUSTOMS, COMPLY and PACKAGE with
/// the legacy statement texts (VATCODE reads fin.VatCode.RatePct, absent from the repository schema, so it ends RC 12
/// SQL0001 wherever the live schema matches the repository - BC-ITM-04). Read-only. VATSET, CUSTCHK and DGCHECK are deferred
/// (SME-1454).
/// </summary>
internal sealed class ItmComplProgram(Sqlio sql)
{
    private bool _error;

    public async Task RunAsync(CaHeader ca, ItmcomplArea co, CancellationToken ct)
    {
        _error = false;
        Initialise(ca, co);
        // 0200-VALIDATE
        if (Cob.Spaces(ca.Company) || Cob.Spaces(co.ItemCode))
        {
            ca.ReasonCode = "CMP0001";
            ca.SetMessage("A company and an item are required");
            Reject(ca, co);
        }
        if (_error) return;
        switch (ca.Function)
        {
            case "VATCODE":
                await VatCodeAsync(ca, co, ct);
                break;
            case "CUSTOMS":
                await CustomsAsync(ca, co, ct);
                break;
            case "COMPLY":
                await ComplianceAsync(ca, co, ct);
                break;
            case "PACKAGE":
                await PackagingAsync(ca, co, ct);
                break;
            case "VATSET":
            case "CUSTCHK":
            case "DGCHECK":
                throw new NotSupportedException($"ITMCOMPL {ca.Function} is deferred (STERP-ITM.md FR-STERP-ITM-027, SME-1454).");
            default:
                ca.ReasonCode = "CMP0009";
                ca.SetMessage("ITMCOMPL does not support function " + Cob.Trim(ca.Function));
                Reject(ca, co);
                break;
        }
    }

    private static void Initialise(CaHeader ca, ItmcomplArea co)
    {
        ca.Program = "ITMCOMPL";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "ITM-025";
        co.VatRate = co.DutyRate = co.NetWeightKg = 0;
        co.VatFrom = co.VatTo = co.CustomsFrom = co.LineCnt = co.FaultCnt = 0;
        co.PkgTotalG = co.PkgPlasticG = co.PkgRecycledG = 0;
        co.VatCode = co.VatEvidence = co.DrcWording = co.CommodityCode = co.OriginCountry = "";
        co.PrefEvidence = co.ExportLicence = co.SuppUnit = co.ConformityRef = "";
        co.DgUnNumber = co.DgClass = co.DgPackGroup = co.ReachStatus = co.WeeeCategory = co.BatteryCategory = "";
        co.Valid = co.Preferential = co.ExportControl = co.UkcaMarked = co.DrcGoods = co.PptLiable = "N";
        co.ItemCode = Cob.Upper(co.ItemCode, 40);
        if (Cob.Spaces(co.Direction)) co.Direction = "SALE";
        if (co.TaxPoint == 0) co.TaxPoint = ca.BusinessDate;
        if (co.TaxPoint == 0) co.TaxPoint = 20261005;
        if (co.DespatchQty == 0) co.DespatchQty = 1;
    }

    // ------------------------------------------------------------------ 1000 VATCODE

    private async Task VatCodeAsync(CaHeader ca, ItmcomplArea co, CancellationToken ct)
    {
        await ReadDrcFlagAsync(ca, co, ct);
        if (ca.Failed) return;
        var tp = Cob.D8(co.TaxPoint);
        var r = await sql.SingleAsync(
            "SELECT TOP 1 v.VatCode, ISNULL(c.RatePct,0), ISNULL(v.EvidenceRef,''), CONVERT(char(8), v.EffectiveFrom, 112), " +
            "ISNULL(CONVERT(char(8), v.EffectiveTo, 112),'') FROM inv.ItemVatClass v LEFT JOIN fin.VatCode c ON c.VatCode = v.VatCode " +
            "WHERE v.CompanyCode = @p1 AND v.ItemCode = @p2 AND v.Direction = @p3 AND v.EffectiveFrom <= CONVERT(date, @p4, 112) AND " +
            "(v.EffectiveTo IS NULL OR v.EffectiveTo >= CONVERT(date, @p5, 112)) ORDER BY v.EffectiveFrom DESC",
            [ca.Company, co.ItemCode, co.Direction, tp, tp], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound)
        {
            ca.ReasonCode = "CMP0002";
            ca.SetMessage("Item " + Cob.Trim(co.ItemCode) + " has no UK VAT classification at that tax point, so it cannot be invoiced");
            Reject(ca, co);
            return;
        }
        co.VatCode = Col.Text(r.Row, 0, 4);
        co.VatRate = CobolNumeric.Store(Col.Num(r.Row, 1), 3, 4);
        co.VatEvidence = Col.Text(r.Row, 2);
        co.VatFrom = Date8(Col.Num(r.Row, 3));
        co.VatTo = Cob.Spaces(Col.Text(r.Row, 4)) ? 0 : Date8(Col.Num(r.Row, 4));
        if (co.DrcGoods == "Y" && co.Direction == "SALE")
        {
            co.VatCode = "DRC";
            co.VatRate = 0;
            co.DrcWording = "Reverse charge: customer to pay the VAT to HMRC (VAT Act 1994 section 55A)";
        }
        if (co.VatCode != "S" && Cob.Spaces(co.VatEvidence))
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "CMP0003";
            ca.AddMessage();
        }
        co.Valid = "Y";
        ca.SetMessage("VAT code " + Cob.Trim(co.VatCode) + " at " + Cob.NumEd(co.VatRate) + "% for " + Cob.Trim(co.Direction));
    }

    /// <summary>1100-READ-DRC-FLAG: the item's compliance attributes.</summary>
    private async Task ReadDrcFlagAsync(CaHeader ca, ItmcomplArea co, CancellationToken ct)
    {
        var r = await sql.SingleAsync(
            "SELECT IsDrcSpecifiedGoods, UkcaMarked, ISNULL(ConformityDocRef,''), ISNULL(DangerousGoodsUn,''), " +
            "ISNULL(DangerousGoodsClass,''), ISNULL(DangerousGoodsPackGroup,''), ISNULL(ReachStatus,''), ISNULL(WeeeCategory,''), " +
            "ISNULL(BatteryCategory,''), ISNULL(NetWeightKg,0) FROM inv.Item WHERE CompanyCode = @p1 AND ItemCode = @p2",
            [ca.Company, co.ItemCode], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound)
        {
            ca.ReasonCode = "CMP0001";
            ca.SetMessage("Item " + Cob.Trim(co.ItemCode) + " is not held in company " + Cob.Trim(ca.Company));
            ca.ReturnCode = 16;
            _error = true;
            ca.AddMessage();
            return;
        }
        var row = r.Row;
        if (Col.Bit(row, 0)) co.DrcGoods = "Y";
        if (Col.Bit(row, 1)) co.UkcaMarked = "Y";
        co.ConformityRef = Col.Text(row, 2, 40);
        co.DgUnNumber = Col.Text(row, 3, 8);
        co.DgClass = Col.Text(row, 4, 8);
        co.DgPackGroup = Col.Text(row, 5, 4);
        co.ReachStatus = Col.Text(row, 6, 20);
        co.WeeeCategory = Col.Text(row, 7, 20);
        co.BatteryCategory = Col.Text(row, 8, 20);
        co.NetWeightKg = CobolNumeric.Store(Col.Num(row, 9), 9, 6);
    }

    // ------------------------------------------------------------------ 3000 CUSTOMS

    private async Task CustomsAsync(CaHeader ca, ItmcomplArea co, CancellationToken ct)
    {
        await ReadDrcFlagAsync(ca, co, ct);
        if (ca.Failed) return;
        var tp = Cob.D8(co.TaxPoint);
        var r = await sql.SingleAsync(
            "SELECT TOP 1 CommodityCode, CountryOfOrigin, PreferentialOrigin, ISNULL(PreferenceEvidenceRef,''), ISNULL(DutyRatePct,0), " +
            "ExportControlled, ISNULL(ExportLicenceRef,''), ISNULL(SupplementaryUnit,''), ISNULL(NetWeightKgPerStockUnit,0), " +
            "CONVERT(char(8), EffectiveFrom, 112) FROM inv.ItemCustoms WHERE CompanyCode = @p1 AND ItemCode = @p2 AND EffectiveFrom <= " +
            "CONVERT(date, @p3, 112) AND (EffectiveTo IS NULL OR EffectiveTo >= CONVERT(date, @p4, 112)) ORDER BY EffectiveFrom DESC",
            [ca.Company, co.ItemCode, tp, tp], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound)
        {
            ca.ReasonCode = "CMP0004";
            ca.SetMessage("Item " + Cob.Trim(co.ItemCode) + " has no customs data effective at that date");
            ca.ReturnCode = 16;
            _error = true;
            ca.AddMessage();
            return;
        }
        var row = r.Row;
        co.CommodityCode = Col.Text(row, 0, 10);
        co.OriginCountry = Col.Text(row, 1, 2);
        if (Col.Bit(row, 2)) co.Preferential = "Y";
        co.PrefEvidence = Col.Text(row, 3, 60);
        co.DutyRate = CobolNumeric.Store(Col.Num(row, 4), 3, 4);
        if (Col.Bit(row, 5)) co.ExportControl = "Y";
        co.ExportLicence = Col.Text(row, 6, 40);
        co.SuppUnit = Col.Text(row, 7, 10);
        var net = Col.Num(row, 8);
        if (net > 0) co.NetWeightKg = CobolNumeric.Store(net, 9, 6);
        co.CustomsFrom = Date8(Col.Num(row, 9));
        co.Valid = "Y";
        ca.SetMessage("Commodity " + Cob.Trim(co.CommodityCode) + ", origin " + CobolText.Fit(co.OriginCountry, 2));
    }

    // ------------------------------------------------------------------ 4000 COMPLY

    private async Task ComplianceAsync(CaHeader ca, ItmcomplArea co, CancellationToken ct)
    {
        await ReadDrcFlagAsync(ca, co, ct);
        if (ca.Failed) return;
        co.FaultCnt = 0;
        co.Valid = "Y";
        if (co.UkcaMarked == "Y" && Cob.Spaces(co.ConformityRef))
        {
            co.FaultCnt++;
            co.Valid = "N";
            ca.ReasonCode = "CMP0008";
            ca.SetMessage("A UKCA marked item must hold its declaration of conformity reference");
        }
        if (!Cob.Spaces(co.DgUnNumber) && (Cob.Spaces(co.DgClass) || Cob.Spaces(co.DgPackGroup)))
        {
            co.FaultCnt++;
            co.Valid = "N";
            ca.ReasonCode = "CMP0008";
            ca.SetMessage("Dangerous goods need a class and a packing group as well as the UN number");
        }
        if (co.FaultCnt > 0)
        {
            Reject(ca, co);
            return;
        }
        ca.SetMessage("Compliance data complete for " + Cob.Trim(co.ItemCode));
    }

    // ------------------------------------------------------------------ 5000 PACKAGE

    private async Task PackagingAsync(CaHeader ca, ItmcomplArea co, CancellationToken ct)
    {
        var r = await sql.QueryAsync(
            "SELECT PackagingMaterial, PackagingType, WeightGrams, ISNULL(RecycledContentPct,0), IsPlasticPackagingTax FROM " +
            "inv.ItemPackaging WHERE CompanyCode = @p1 AND ItemCode = @p2 ORDER BY PackagingMaterial, PackagingType",
            [ca.Company, co.ItemCode], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        co.LineCnt = 0;
        foreach (var row in r.Rows!)
        {
            // 5100-ADD-PACKAGING: at most 12 lines are held
            if (co.LineCnt >= 12) continue;
            var l = co.Lines[co.LineCnt++];
            l.Material = Col.Text(row, 0, 20);
            l.Type = Col.Text(row, 1, 20);
            l.WeightG = Cob.Q(Col.Num(row, 2));
            l.Recycled = CobolNumeric.Store(Col.Num(row, 3), 3, 4);
            l.TotalG = Cob.QR(l.WeightG * co.DespatchQty);
            l.RecycG = Cob.QR(l.TotalG * l.Recycled / 100m);
            if (Col.Bit(row, 4))
            {
                l.Ppt = "Y";
                co.PptLiable = "Y";
            }
            else
            {
                l.Ppt = "N";
            }
            co.PkgTotalG = Cob.Q(co.PkgTotalG + l.TotalG);
            if (l.Material == "PLASTIC")
            {
                co.PkgPlasticG = Cob.Q(co.PkgPlasticG + l.TotalG);
                co.PkgRecycledG = Cob.Q(co.PkgRecycledG + l.RecycG);
            }
        }
        if (co.LineCnt == 0)
        {
            ca.ReturnCode = 16;
            ca.SetMessage("No packaging composition is recorded for " + Cob.Trim(co.ItemCode));
            return;
        }
        co.Valid = "Y";
        var text = Cob.QtyEd(Cob.Q(co.PkgPlasticG / 1000m)) + " kg of plastic, of which";
        ca.SetMessage(text + " " + Cob.QtyEd(Cob.Q(co.PkgRecycledG / 1000m)) + " kg is recycled");
    }

    // ------------------------------------------------------------------ 9000 outcomes

    private void Reject(CaHeader ca, ItmcomplArea co)
    {
        _error = true;
        co.Valid = "N";
        ca.ReturnCode = 8;
        ca.AddMessage();
    }

    private void SqlFailed(CaHeader ca, SqlioOutcome r)
    {
        _error = true;
        ca.SetMessage(r.Message200("ITMCOMPL"));
        ca.ReasonCode = "SQL0001";
        ca.ReturnCode = 12;
        ca.AddMessage();
    }

    /// <summary>COMPUTE into PIC 9(8) from NUMVAL.</summary>
    private static int Date8(decimal v) => (int)(Math.Abs(decimal.Truncate(v)) % 100_000_000m);
}
