using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>
/// ARVATWRT (cobol/online/ar/ARVATWRT.cob, AR-023) - LOOKUP: the active VAT code, its rate at the tax point and the VAT
/// return boxes for the direction; WRITE: LOOKUP then one fin.VatTransaction row (none when net and VAT are both zero).
/// No unit of work of its own: the INSERT runs in the caller's.
/// </summary>
public sealed class VatWriterEngine(ISqlRunner sql)
{
    private const string Program = "ARVATWRT";

    public async Task<LegacyResult> CallAsync(ArvatwrtArea vw, CancellationToken ct = default)
    {
        // 0100-INITIALISE
        var c = ArCtl.Start(vw.Header, Program, "AR-023");
        vw.RateFound = "N";
        vw.VatCode = CobolText.Trimmed(vw.VatCode.ToUpperInvariant(), 4);
        if (CobolText.IsSpaces(vw.Direction)) vw.Direction = "OUTPUT";
        if (CobolText.IsSpaces(vw.Currency)) vw.Currency = "GBP";
        if (vw.FxRate == 0) vw.FxRate = 1;
        if (vw.TaxPoint == 0) vw.TaxPoint = c.BusDate;
        if (vw.PostingDate == 0) vw.PostingDate = vw.TaxPoint;
        if (CobolText.IsSpaces(vw.SourceModule)) vw.SourceModule = "AR";
        if (CobolText.IsSpaces(vw.PartyType)) vw.PartyType = "CUSTOMER";
        if (CobolText.IsSpaces(vw.PeriodKey))
        {
            // 0150-PERIOD-KEY: yyyy "Q" quarter
            var d = ArEdit.D8(vw.TaxPoint);
            var mm = int.Parse(d[4..6], System.Globalization.CultureInfo.InvariantCulture);
            var qtr = (int)CobolNumeric.Store((mm - 1) / 3m + 1, 2, 0);
            vw.PeriodKey = d[..4] + "Q" + (Math.Abs(qtr) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // 0200-VALIDATE
        if (CobolText.IsSpaces(vw.VatCode) || CobolText.IsLowValues(vw.VatCode))
        {
            c.Reject("VAT0001", "Every invoice line needs a VAT code");
        }
        else if (c.Func == "WRITE" && (CobolText.IsSpaces(vw.DocRef) || CobolText.IsSpaces(c.Co)))
        {
            c.Reject("VAT0002", "A VAT record needs a company and a document reference");
        }
        if (!c.InputBad)
        {
            switch (c.Func)
            {
                case "LOOKUP":
                    await LookupAsync(vw, c, ct);
                    break;
                case "WRITE":
                    await LookupAsync(vw, c, ct);
                    await WriteAsync(vw, c, ct);
                    break;
                default:
                    c.Rc = 8;
                    c.Reason = "VAT0008";
                    c.Msg = "ARVATWRT does not support function [" + c.Func.Trim(' ') + "]";
                    break;
            }
        }
        c.SetResult(vw.Header);
        return vw.Header.Outcome();
    }

    /// <summary>1000-LOOKUP-CODE.</summary>
    private async Task LookupAsync(ArvatwrtArea vw, ArCtl c, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT v.Liability, v.Mechanism, ISNULL(v.InvoiceWording,''), CONVERT(varchar(4), ISNULL(v.OutputVatBox,0)), " +
                "CONVERT(varchar(4), ISNULL(v.OutputNetBox,0)), CONVERT(varchar(4), ISNULL(v.InputVatBox,0)), " +
                "CONVERT(varchar(4), ISNULL(v.InputNetBox,0)), CONVERT(char(1), v.SelfAccounts), CONVERT(char(1), v.EvidenceRequired), " +
                "CONVERT(varchar(20), ISNULL((SELECT TOP (1) r.RatePct FROM fin.VatRate r WHERE r.VatCode = v.VatCode AND " +
                "r.EffectiveFrom <= ? ORDER BY r.EffectiveFrom DESC), -1)), CONVERT(char(1), v.IsRecoverable) FROM fin.VatCode v " +
                "WHERE v.VatCode = ? AND v.IsActive = 1",
                [SqlioParm.Digits(vw.TaxPoint, 8), SqlioParm.Text(vw.VatCode)], [12, 12, 200, 4, 4, 4, 4, 2, 2, 20, 2], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "VAT0003";
            c.Msg = "VAT code " + vw.VatCode.Trim(' ') + " is not an active code";
            return;
        }
        vw.Liability = CobolText.Trimmed(row.Raw(0), 12);
        vw.Mechanism = CobolText.Trimmed(row.Raw(1), 12);
        vw.Wording = CobolText.Trimmed(row.Raw(2), 200);
        vw.SelfAccounts = CobolText.Fit(row.Raw(7), 1);
        vw.EvidenceReq = CobolText.Fit(row.Raw(8), 1);
        var pct = CobolNumeric.Store(row.NumVal(9), 5, 6);
        if (pct < 0)
        {
            c.Rc = 8;
            c.Reason = "VAT0004";
            c.Msg = "No VAT rate is held for code " + vw.VatCode.Trim(' ') + " on that tax point";
            return;
        }
        vw.RatePct = CobolNumeric.Store(pct, 3, 4);
        vw.RateFound = "Y";
        if (vw.Direction == "OUTPUT")
        {
            vw.VatBox = (int)CobolNumeric.Store(row.NumVal(3), 2, 0, signed: false);
            vw.NetBox = (int)CobolNumeric.Store(row.NumVal(4), 2, 0, signed: false);
        }
        else
        {
            vw.VatBox = (int)CobolNumeric.Store(row.NumVal(5), 2, 0, signed: false);
            vw.NetBox = (int)CobolNumeric.Store(row.NumVal(6), 2, 0, signed: false);
        }
    }

    /// <summary>2000-WRITE-RECORD.</summary>
    private async Task WriteAsync(ArvatwrtArea vw, ArCtl c, CancellationToken ct)
    {
        if (c.RcBad) return;
        if (vw.NetGbp == 0 && vw.VatGbp == 0) return;
        try
        {
            await sql.ExecAsync(
                "INSERT fin.VatTransaction (CompanyCode, SourceModule, DocumentType, DocumentRef, LineNum, PartyType, PartyCode, " +
                "PartyVatRegNo, TaxPointDate, PostingDate, VatPeriodKey, JournalId, VatCode, RatePct, Mechanism, Direction, CurrencyCode, " +
                "FxRate, NetAmount, VatAmount, NetAmountGBP, VatAmountGBP, RecoverableVatGBP, VatBox, NetBox, PlaceOfSupply, EvidenceRef, " +
                "IsLateEntry, AdjustmentType) VALUES (?, ?, ?, ?, NULLIF(?,0), ?, NULLIF(?,''), NULLIF(?,''), ?, ?, ?, NULLIF(?,0), ?, ?, ?, " +
                "?, ?, ?, ?, ?, ?, ?, ?, NULLIF(?,0), NULLIF(?,0), NULLIF(?,''), NULLIF(?,''), ?, NULLIF(?,''))",
                [
                    SqlioParm.Text(c.Co), SqlioParm.Text(vw.SourceModule), SqlioParm.Text(vw.DocType), SqlioParm.Text(vw.DocRef),
                    SqlioParm.Text(ArEdit.Int(vw.LineNum)), SqlioParm.Text(vw.PartyType), SqlioParm.Text(vw.PartyCode),
                    SqlioParm.Text(vw.PartyVatNo), SqlioParm.Digits(vw.TaxPoint, 8), SqlioParm.Digits(vw.PostingDate, 8),
                    SqlioParm.Text(vw.PeriodKey), SqlioParm.Text(ArEdit.Int(vw.JournalId)), SqlioParm.Text(vw.VatCode),
                    SqlioParm.Text(ArEdit.Money(vw.RatePct)), SqlioParm.Text(vw.Mechanism), SqlioParm.Text(vw.Direction),
                    SqlioParm.Text(vw.Currency), SqlioParm.Text(ArEdit.Rate(vw.FxRate)), SqlioParm.Text(ArEdit.Money(vw.Net)),
                    SqlioParm.Text(ArEdit.Money(vw.Vat)), SqlioParm.Text(ArEdit.Money(vw.NetGbp)), SqlioParm.Text(ArEdit.Money(vw.VatGbp)),
                    SqlioParm.Text(ArEdit.Money(vw.RecoverableGbp)), SqlioParm.Text(ArEdit.Int(vw.VatBox)), SqlioParm.Text(ArEdit.Int(vw.NetBox)),
                    SqlioParm.Text(vw.PlaceOfSupply), SqlioParm.Text(vw.EvidenceRef), SqlioParm.Text(vw.LateEntry == "Y" ? "1" : "0"),
                    SqlioParm.Text(vw.AdjustType),
                ], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        c.Msg = "VAT record written for " + vw.DocRef.Trim(' ') + " code " + vw.VatCode.Trim(' ');
    }
}
