using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>
/// ARPOSTGL (cobol/online/ar/ARPOSTGL.cob) - the sales ledger route into the nominal: resolves each leg of a business
/// event to its approved account, stages the lines in fin.JournalStage through the platform posting gateway and calls
/// fin.usp_PostJournal. No BEGN/COMT: staging and posting join the caller's unit of work. G09-ADV-18 is reproduced (the
/// fifth result column - PeriodNo - is read as the duplicate flag). The procedure call binds the rate as AR-RATE-ED
/// -(5)9.9(8) (8 decimals, @FxRate decimal(18,8)), which the gateway's 6-decimal rate binding cannot express, so it is
/// executed here with the gateway's statement form.
/// </summary>
public sealed class ArGlPostingEngine(ISqlRunner sql, IGlPostingGateway gateway)
{
    private const string Program = "ARPOSTGL";

    private sealed class MapRow
    {
        public string Leg = "";
        public string Account = "";
        public string Cc = "";
        public string Proj = "";
    }

    public async Task<LegacyResult> CallAsync(ArpostglArea pg, CancellationToken ct = default)
    {
        var c = ArCtl.Start(pg.Header, Program, "AR-004");
        c.Site = "";
        Initialise(c, pg);
        Validate(c, pg);
        if (!c.InputBad)
        {
            switch (c.Func)
            {
                case "ACCOUNT":
                {
                    var map = new MapRow { Leg = CobolText.Trimmed(pg.LookupLeg, 12) };
                    await ReadMapAsync(c, pg, map, ct);
                    if (!c.RcBad) pg.LookupAccount = map.Account;
                    break;
                }
                case "POST":
                    await PostJournalAsync(c, pg, ct);
                    break;
                default:
                    c.Rc = 8;
                    c.Reason = "GLP0010";
                    c.Msg = "ARPOSTGL does not support function [" + CobolText.Trim(c.Func) + "]";
                    break;
            }
        }
        c.SetResult(pg.Header);
        return pg.Header.Outcome();
    }

    private static void Initialise(ArCtl c, ArpostglArea pg)
    {
        pg.JournalNo = "";
        pg.JournalId = 0;
        pg.TotalDebit = pg.TotalCredit = 0m;
        pg.WasDuplicate = "N";
        if (CobolText.IsSpaces(pg.Currency) || CobolText.IsLowValues(pg.Currency)) pg.Currency = "GBP";
        if (CobolText.IsSpaces(pg.SourceModule)) pg.SourceModule = "AR";
        if (CobolText.IsSpaces(pg.JournalType)) pg.JournalType = "AR";
        if (CobolText.IsSpaces(pg.SeriesCode)) pg.SeriesCode = "JNL";
        if (pg.PostingDate == 0) pg.PostingDate = c.BusDate;
        if (pg.DocDate == 0) pg.DocDate = pg.PostingDate;
        if (CobolText.IsSpaces(pg.OwnTran) || CobolText.IsLowValues(pg.OwnTran)) pg.OwnTran = "N";
    }

    private static void Validate(ArCtl c, ArpostglArea pg)
    {
        if (CobolText.IsSpaces(c.Co))
        {
            c.Reject("GLP0001", "A company code is required to post");
            return;
        }
        if (CobolText.IsSpaces(pg.EventCode) || CobolText.IsLowValues(pg.EventCode))
        {
            c.Reject("GLP0002", "A business event code is required so that the approved account mapping can be used");
            return;
        }
        if (c.Func == "POST" && CobolText.IsSpaces(c.Correl))
        {
            c.Reject("GLP0003", "A correlation id is required so that a repeated posting has no second effect");
            return;
        }
        if (c.Func == "POST" && (pg.LegCnt < 2 || pg.LegCnt > 30))
        {
            c.Reject("GLP0004", "A journal needs between two and thirty legs");
        }
    }

    private async Task ReadMapAsync(ArCtl c, ArpostglArea pg, MapRow map, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT m.LegCode, m.AccountCode, m.DrCr, ISNULL(m.DimCostCentre,''), ISNULL(m.DimProject,'') " +
                "FROM fin.EventAccountMap m WHERE m.CompanyCode = ? AND m.EventCode = ? AND m.LegCode = ? AND m.IsActive = 1",
                [SqlioParm.Text(c.Co), SqlioParm.Text(pg.EventCode), SqlioParm.Text(map.Leg)], [12, 12, 2, 20, 20], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            // the SNGL fetch leaves the row area as it was (WS-M-LEG keeps the leg asked for)
            c.Rc = 8;
            c.Reason = "GLP0005";
            c.Msg = "No approved account mapping exists for event " + CobolText.Trim(pg.EventCode) + " leg " + CobolText.Trim(map.Leg);
            c.MsgCnt++;
            return;
        }
        map.Leg = row.Text(0);
        map.Account = row.Text(1);
        map.Cc = row.Text(3);
        map.Proj = row.Text(4);
    }

    /// <summary>2000: stage every leg then hand the journal to Finance.</summary>
    private async Task PostJournalAsync(ArCtl c, ArpostglArea pg, CancellationToken ct)
    {
        // 2050: a retried attempt may have left staged lines behind
        var clear = await gateway.ClearStageAsync(c.Correl, ct);
        if (clear.Failed)
        {
            c.SqlFailed(clear.ErrorText);
            return;
        }
        var staged = await StageLegsAsync(c, pg, ct);
        if (c.RcBad) return;
        if (staged < 2)
        {
            c.Rc = 8;
            c.Reason = "GLP0006";
            c.Msg = "A journal with fewer than two value bearing legs cannot be posted";
            return;
        }
        if (pg.TotalDebit != pg.TotalCredit)
        {
            c.Rc = 8;
            c.Reason = "GLP0007";
            c.Msg = "The sales ledger journal does not balance; debits are GBP " + ArEdit.MoneyUk(pg.TotalDebit);
            return;
        }
        await CallPostAsync(c, pg, ct);
    }

    private async Task<int> StageLegsAsync(ArCtl c, ArpostglArea pg, CancellationToken ct)
    {
        pg.TotalDebit = pg.TotalCredit = 0m;
        var lineNo = 0;
        var staged = 0;
        var map = new MapRow();
        for (var i = 0; i < pg.LegCnt && i < ArpostglArea.MaxLegs && !c.RcBad; i++)
        {
            var leg = pg.Legs[i];
            if (leg.GDebit == 0 && leg.GCredit == 0) continue;
            map.Cc = map.Proj = "";
            if (CobolText.IsSpaces(leg.GAccount) || CobolText.IsLowValues(leg.GAccount) || CobolText.IsSpaces(leg.GCostCentre))
            {
                map.Leg = CobolText.Trimmed(leg.GLegCode, 12);
                await ReadMapAsync(c, pg, map, ct);
                if (c.RcBad) break;
                if (CobolText.IsSpaces(leg.GAccount) || CobolText.IsLowValues(leg.GAccount)) leg.GAccount = map.Account;
                if (CobolText.IsSpaces(leg.GCostCentre)) leg.GCostCentre = map.Cc;
                if (CobolText.IsSpaces(leg.GProject)) leg.GProject = map.Proj;
            }
            lineNo++;
            staged++;
            pg.TotalDebit = CobolNumeric.Store(pg.TotalDebit + leg.GDebit, 11, 2);
            pg.TotalCredit = CobolNumeric.Store(pg.TotalCredit + leg.GCredit, 11, 2);
            var line = new JournalStageLine(c.Correl, lineNo)
                .Text("CompanyCode", c.Co)
                .Text("AccountCode", leg.GAccount)
                .Parm("DebitAmount", SqlioParm.Text(ArEdit.Money(leg.GDebit)))
                .Parm("CreditAmount", SqlioParm.Text(ArEdit.Money(leg.GCredit)))
                .Text("CurrencyCode", pg.Currency)
                .NullIfBlank("DimCostCentre", leg.GCostCentre)
                .NullIfBlank("DimProject", leg.GProject)
                .Text("Narrative", leg.GNarrative)
                .NullIfBlank("VatCode", leg.GVatCode)
                .NullIfBlank("SubledgerType", leg.GSubledger)
                .NullIfBlank("SubledgerRef", leg.GSubref)
                .Text("SourceRef", pg.SourceRef)
                .NullIfBlank("BankAccountCode", leg.GBankAcct)
                .Text("CreatedBy", c.UserId);
            var staged1 = await gateway.StageLineAsync(line, ct);
            if (staged1.Failed) c.SqlFailed(staged1.ErrorText);
        }
        return staged;
    }

    /// <summary>
    /// 2300: fin.usp_PostJournal owns the balance, period, account, dimension and event mapping rules; its THROW is a
    /// business refusal (RC 08 GLP0011 with the driver text) and dooms the caller's transaction (XACT_ABORT).
    /// </summary>
    private async Task CallPostAsync(ArCtl c, ArpostglArea pg, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "EXEC fin.usp_PostJournal ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?",
                [SqlioParm.Text(c.Co), SqlioParm.Text(c.Correl), SqlioParm.Text(c.UserId), SqlioParm.Text(pg.Description),
                 SqlioParm.Digits(pg.PostingDate, 8), SqlioParm.Text(pg.JournalType), SqlioParm.Digits(pg.DocDate, 8),
                 SqlioParm.Text(pg.Currency), SqlioParm.Text(pg.SourceModule), SqlioParm.Text(pg.SourceRef),
                 SqlioParm.Text(pg.EventCode), SqlioParm.Text(pg.SeriesCode),
                 pg.Currency == "GBP" || pg.FxRate == 0 ? SqlioParm.Null : SqlioParm.Text(ArEdit.Rate(pg.FxRate)),
                 SqlioParm.Text(pg.EvidenceRef)],
                [24, 30, 24, 24, 4], ct);
        }
        catch (SqlioException ex)
        {
            // 9400-POST-FAILED: a refusal from fin.usp_PostJournal is a business message, not a system failure
            c.Rc = 8;
            c.Reason = "GLP0011";
            c.Msg = ex.DriverText;
            c.MsgCnt++;
            return;
        }
        if (!row.Found)
        {
            c.Rc = 12;
            c.Reason = "GLP0008";
            c.Msg = "The journal posting returned no result row";
            return;
        }
        pg.JournalId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        pg.JournalNo = row.Text(1);
        if (row.Raw(4).StartsWith('1'))
        {
            pg.WasDuplicate = "Y";
            c.Rc = 4;
            c.Reason = "GLP0009";
            c.Msg = "That instruction was already posted as journal " + CobolText.Trim(pg.JournalNo);
            c.MsgCnt++;
        }
        else
        {
            c.Msg = "Journal " + CobolText.Trim(pg.JournalNo) + " posted";
        }
    }
}
