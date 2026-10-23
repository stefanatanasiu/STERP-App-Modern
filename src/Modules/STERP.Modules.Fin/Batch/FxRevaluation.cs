using Sterp.Modules.Fin.Common;
using Sterp.Modules.Fin.Journals;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Fin.Batch;

/// <summary>FXREVALCA body (cobol/copy/FXREVALCA.cpy) as REVALRUN fills and reads it.</summary>
internal sealed class FxRevalArea
{
    public string Currency { get; set; } = "";
    public string Account { get; set; } = "";
    public int AsAtDate { get; set; }
    public int FiscalYear { get; set; }
    public int PeriodNo { get; set; }
    public string RateType { get; set; } = "";
    public decimal ForeignAmount { get; set; }
    public decimal RateOriginal { get; set; }
    public decimal RateAtDate { get; set; }
    public decimal CarryingGbp { get; set; }
    public string SubledgerType { get; set; } = "";
    public string SubledgerRef { get; set; } = "";
    public string Position { get; set; } = "";
    public string Mode { get; set; } = "";
    public long RevalId { get; set; }
    public decimal OriginalGbp { get; set; }
    public decimal RevaluedGbp { get; set; }
    public decimal AdjustmentGbp { get; set; }
    public decimal UnrealisedGbp { get; set; }
    public string AdjustType { get; set; } = "";
    public string GainOrLoss { get; set; } = "";
    public decimal TotalGain { get; set; }
    public decimal TotalLoss { get; set; }
    public decimal NetAdjustment { get; set; }
    public string JournalNumber { get; set; } = "";
    public string RevalStatus { get; set; } = "";
}

/// <summary>
/// FXREVAL (cobol/online/fin/FXREVAL.cob; FR-STERP-FIN-056) as the REVALRUN job calls it: CARRY (transaction-rate sterling),
/// REVALUE (closing-rate value and the unrealised adjustment, sign reversed for a liability) and POST (one fin.FxRevaluation
/// header per company/year/period/as-at, the adjustment pair against 7110 staged under FXR-&lt;co&gt;-&lt;asat&gt;-&lt;account&gt;-&lt;ccy&gt;
/// and posted through JNLPOST, header marked POSTED and a revaluation line written). Rates come from FXCONV GETRATE
/// (FCYPERGBP, no inversion). No unit of work of its own (each statement autocommits). REALISE and REVERSE are not reached
/// from any FIN operation or job and are not exposed.
/// </summary>
internal sealed class FxRevaluation(ISqlRunner sql, IFxConverter fxconv, JournalPostingProgram jnlpost)
{
    private const string Program = "FXREVAL";
    private string _correl = "";

    public async Task<CaHeader> RunAsync(CaRequest req, FxRevalArea fr, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-020");
        Initialise(req, fr);
        switch (req.Function)
        {
            case "CARRY":
                await CarryAsync(req, ca, fr, ct);
                break;
            case "REVALUE":
                await RevalueAsync(req, ca, fr, ct);
                break;
            case "POST":
                await PostRunAsync(req, ca, fr, ct);
                break;
            default:
                ca.ReasonCode = "FXR0009";
                ca.Message = "FXREVAL does not support function " + req.Function.Trim();
                Reject(ca);
                break;
        }
        return ca;
    }

    // 0100-INITIALISE
    private static void Initialise(CaRequest req, FxRevalArea fr)
    {
        fr.OriginalGbp = fr.RevaluedGbp = fr.AdjustmentGbp = fr.UnrealisedGbp = 0;
        fr.TotalGain = fr.TotalLoss = fr.NetAdjustment = 0;
        fr.AdjustType = fr.GainOrLoss = fr.JournalNumber = fr.RevalStatus = "";
        if (CobolText.IsSpaces(fr.RateType)) fr.RateType = "SPOT";
        if (fr.AsAtDate == 0) fr.AsAtDate = req.BusinessDate;
        if (CobolText.IsSpaces(fr.Position)) fr.Position = "ASSET";
        fr.Currency = CobolText.Trimmed(CobolText.Upper(fr.Currency), 3);
    }

    // 1000-CARRY
    private async Task CarryAsync(CaRequest req, CaHeader ca, FxRevalArea fr, CancellationToken ct)
    {
        if (CobolText.IsSpaces(fr.Currency) || fr.Currency == "GBP")
        {
            ca.ReasonCode = "FXR0001";
            ca.Message = "A foreign currency is required for a revaluation";
            Reject(ca);
            return;
        }
        if (fr.RateOriginal == 0) fr.RateOriginal = await GetRateAsync(req, ca, fr, ct);
        if (fr.RateOriginal == 0)
        {
            ca.ReasonCode = "FXR0002";
            ca.Message = "No positive exchange rate is held, so the sterlin";
            Reject(ca);
            return;
        }
        fr.OriginalGbp = CobolNumeric.Store(CobolNumeric.RoundAway(fr.ForeignAmount / fr.RateOriginal, 2), 13, 2);
        if (fr.CarryingGbp == 0) fr.CarryingGbp = fr.OriginalGbp;
        ca.Message = CobolText.Trimmed(fr.Currency.Trim() + " carried at " + FinEdit.Money(fr.OriginalGbp) + " GBP at the transaction rate", 200);
    }

    // 1100-GET-RATE: FXCONV GETRATE at the as-at date; a refusal is copied to the caller and the rate is 0
    private async Task<decimal> GetRateAsync(CaRequest req, CaHeader ca, FxRevalArea fr, CancellationToken ct)
    {
        var fx = await fxconv.CallAsync(new KernelCaller(req.Company, "", req.User, req.BusinessDate, ""), "GETRATE", a =>
        {
            a.Currency = fr.Currency;
            a.RateDate = fr.AsAtDate;
            a.RateType = fr.RateType;
            a.QuoteBasis = "FCYPERGBP";
            a.AllowInvert = "N";
        }, ct);
        if (fx.Header.ReturnCode > 4)
        {
            ca.ReasonCode = fx.Header.ReasonCode;
            ca.Message = fx.Header.Message;
            ca.ReturnCode = fx.Header.ReturnCode;
            ca.MsgCount++;
            return 0;
        }
        return CobolNumeric.Store(fx.Rate, 10, 8);
    }

    // 2000-REVALUE
    private async Task RevalueAsync(CaRequest req, CaHeader ca, FxRevalArea fr, CancellationToken ct)
    {
        await CarryAsync(req, ca, fr, ct);
        if (ca.Failed) return;
        if (fr.RateAtDate == 0) fr.RateAtDate = await GetRateAsync(req, ca, fr, ct);
        if (fr.RateAtDate == 0)
        {
            ca.ReasonCode = "FXR0003";
            ca.Message = "No closing rate is held for that revaluation date";
            Reject(ca);
            return;
        }
        fr.RevaluedGbp = CobolNumeric.Store(CobolNumeric.RoundAway(fr.ForeignAmount / fr.RateAtDate, 2), 13, 2);
        fr.AdjustmentGbp = CobolNumeric.Store(fr.RevaluedGbp - fr.CarryingGbp, 13, 2);
        fr.UnrealisedGbp = fr.AdjustmentGbp;
        fr.AdjustType = "UNREALISED";
        if (fr.Position == "LIABILITY") fr.UnrealisedGbp = CobolNumeric.Store(0 - fr.AdjustmentGbp, 13, 2);
        if (fr.UnrealisedGbp < 0)
        {
            fr.GainOrLoss = "LOSS";
            fr.TotalLoss = CobolNumeric.Store(0 - fr.UnrealisedGbp, 13, 2);
        }
        else
        {
            fr.GainOrLoss = "GAIN";
            fr.TotalGain = fr.UnrealisedGbp;
        }
        fr.NetAdjustment = fr.UnrealisedGbp;
        ca.Message = CobolText.Trimmed("Carrying value at the closing rate is " + FinEdit.Money(fr.RevaluedGbp) +
                                       " GBP. The adjustment is unrealised and is reversed at the start of the next period", 200);
    }

    // 4000-POST-RUN
    private async Task PostRunAsync(CaRequest req, CaHeader ca, FxRevalArea fr, CancellationToken ct)
    {
        await RevalueAsync(req, ca, fr, ct);
        if (ca.Failed) return;
        if (CobolText.IsSpaces(fr.Account))
        {
            ca.ReasonCode = "FXR0005";
            ca.Message = "The monetary account being revalued is required";
            Reject(ca);
            return;
        }
        if (fr.UnrealisedGbp == 0)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "FXR0006";
            ca.Message = "The closing rate produces no adjustment, so no jo";
            ca.MsgCount++;
            return;
        }
        await WriteRunAsync(req, ca, fr);
        if (ca.Failed) return;
        _correl = CobolText.Trimmed("FXR-" + req.Company.Trim() + "-" + Disp.U(fr.AsAtDate, 8) + "-" + fr.Account.Trim() + "-" +
                                    fr.Currency.Trim(), 36);
        string dr, cr;
        decimal amount;
        if (fr.UnrealisedGbp > 0)
        {
            dr = fr.Account;
            cr = "7110";
            amount = fr.UnrealisedGbp;
        }
        else
        {
            dr = "7110";
            cr = fr.Account;
            amount = CobolNumeric.Store(0 - fr.UnrealisedGbp, 13, 2);
        }
        await StageOneAsync(req, ca, fr, 1, dr, amount, 0);
        await StageOneAsync(req, ca, fr, 2, cr, 0, amount);
        if (ca.Failed) return;
        await PostAsync(req, ca, fr, ct);
        if (ca.Failed) return;
        await MarkPostedAsync(req, ca, fr);
        fr.RevalStatus = "POSTED";
        ca.Message = CobolText.Trimmed("Revaluation journal " + fr.JournalNumber.Trim() + " posted for " + FinEdit.Money(fr.UnrealisedGbp) +
                                       " GBP unrealised. It must be reversed in the next period", 200);
    }

    // 4100-WRITE-RUN: one header per company/year/period/as-at (OUTPUT of the insert, else 4150-FIND-RUN)
    private async Task WriteRunAsync(CaRequest req, CaHeader ca, FxRevalArea fr)
    {
        object?[]? row;
        try
        {
            row = await sql.SingleAsync(
                "INSERT fin.FxRevaluation (CompanyCode, FiscalYear, PeriodNo, AsAtDate, RateType, TotalGainGBP, TotalLossGBP, " +
                "NetAdjustmentGBP, Status, RunBy) OUTPUT inserted.RevalId, inserted.Status, '' SELECT @p1, @p2, @p3, @p4, @p5, @p6, @p7, " +
                "@p8, 'PREVIEW', @p9 WHERE NOT EXISTS (SELECT 1 FROM fin.FxRevaluation x WHERE x.CompanyCode = @p10 AND x.FiscalYear = " +
                "@p11 AND x.PeriodNo = @p12 AND x.AsAtDate = @p13)",
                req.Company, Disp.U(fr.FiscalYear, 4), Disp.U(fr.PeriodNo, 2), Disp.U(fr.AsAtDate, 8), fr.RateType,
                CobolNumeric.SqlioNumber(fr.TotalGain), CobolNumeric.SqlioNumber(fr.TotalLoss), CobolNumeric.SqlioNumber(fr.NetAdjustment),
                req.User, req.Company, Disp.U(fr.FiscalYear, 4), Disp.U(fr.PeriodNo, 2), Disp.U(fr.AsAtDate, 8));
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is not null) fr.RevalId = (long)CobolNumeric.Store(Slot.NumVal(Slot.Text(row[0], 18)), 9, 0);
        if (fr.RevalId == 0) await FindRunAsync(req, ca, fr);
    }

    // 4150-FIND-RUN
    private async Task FindRunAsync(CaRequest req, CaHeader ca, FxRevalArea fr)
    {
        object?[]? row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CAST(x.RevalId AS varchar(18)), x.Status, ISNULL(x.JournalNumber,'') FROM fin.FxRevaluation x WHERE " +
                "x.CompanyCode = @p1 AND x.FiscalYear = @p2 AND x.PeriodNo = @p3 AND x.AsAtDate = @p4",
                req.Company, Disp.U(fr.FiscalYear, 4), Disp.U(fr.PeriodNo, 2), Disp.U(fr.AsAtDate, 8));
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is null) return;
        fr.RevalId = (long)CobolNumeric.Store(Slot.NumVal(Slot.Text(row[0], 18)), 9, 0);
        fr.RevalStatus = Slot.Text(row[1], 12);
        fr.JournalNumber = Slot.Text(row[2], 30);
    }

    // 4250-STAGE-ONE
    private async Task StageOneAsync(CaRequest req, CaHeader ca, FxRevalArea fr, int lineNo, string account, decimal debit, decimal credit)
    {
        var acct = CobolText.Trimmed(account, 12);
        try
        {
            await sql.ExecAsync(
                "INSERT fin.JournalStage (CorrelationId, LineNum, CompanyCode, AccountCode, DebitAmount, CreditAmount, CurrencyCode, " +
                "DimCostCentre, Narrative, SubledgerType, SubledgerRef, CreatedBy) SELECT @p1, @p2, @p3, @p4, @p5, @p6, 'GBP', " +
                "NULLIF(@p7,''), @p8, NULLIF(@p9,''), NULLIF(@p10,''), @p11 WHERE NOT EXISTS (SELECT 1 FROM fin.JournalStage s WHERE " +
                "s.CorrelationId = @p12 AND s.LineNum = @p13)",
                _correl, CobolNumeric.SqlioInteger(lineNo), req.Company, acct,
                lineNo == 1 ? CobolNumeric.SqlioNumber(debit) : "0", lineNo == 1 ? "0" : CobolNumeric.SqlioNumber(credit),
                acct.StartsWith("7110", StringComparison.Ordinal) ? "ADMIN" : "",
                "Period end revaluation of " + fr.Currency.Trim() + " balances", fr.SubledgerType, fr.SubledgerRef, req.User, _correl,
                CobolNumeric.SqlioInteger(lineNo));
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
        }
    }

    // 4300-POST
    private async Task PostAsync(CaRequest req, CaHeader ca, FxRevalArea fr, CancellationToken ct)
    {
        var jp = new JnlPostArea
        {
            PostingDate = fr.AsAtDate,
            DocumentDate = fr.AsAtDate,
            Currency = "GBP",
            SourceModule = "FIN",
            SourceRef = fr.Currency,
            EventCode = "FIN.REVALUATION",
            JournalType = "REVAL",
            SeriesCode = "JNL",
            Description = "Unrealised revaluation of " + fr.Currency.Trim() + " monetary balances",
            EvidenceRef = "fin.FxRevaluation",
        };
        var jh = await jnlpost.RunAsync(new CaRequest("POST", req.Company, req.Site, req.User, req.BusinessDate, _correl), jp, ct);
        fr.JournalNumber = CobolText.Trimmed(jp.NewJournalNo, 30);
        if (jh.ReturnCode > 4)
        {
            ca.ReasonCode = jh.ReasonCode;
            ca.Message = jh.Message;
            ca.ReturnCode = jh.ReturnCode;
            ca.MsgCount++;
        }
    }

    // 4400-MARK-POSTED: header POSTED with this position's totals, then the revaluation line
    private async Task MarkPostedAsync(CaRequest req, CaHeader ca, FxRevalArea fr)
    {
        var id = CobolNumeric.SqlioInteger(fr.RevalId);
        try
        {
            await sql.ExecAsync(
                "UPDATE fin.FxRevaluation SET Status = 'POSTED', JournalNumber = @p1, CorrelationId = @p2, TotalGainGBP = @p3, " +
                "TotalLossGBP = @p4, NetAdjustmentGBP = @p5 WHERE RevalId = @p6",
                fr.JournalNumber, _correl, CobolNumeric.SqlioNumber(fr.TotalGain), CobolNumeric.SqlioNumber(fr.TotalLoss),
                CobolNumeric.SqlioNumber(fr.NetAdjustment), id);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
        }
        try
        {
            await sql.ExecAsync(
                "INSERT fin.FxRevaluationLine (RevalId, CompanyCode, AccountCode, SubledgerType, SubledgerRef, CurrencyCode, ForeignBalance, " +
                "CarryingAmountGBP, RateOriginal, RateAtDate, RevaluedAmountGBP, AdjustmentGBP, AdjustmentType) VALUES (@p1, @p2, @p3, " +
                "NULLIF(@p4,''), NULLIF(@p5,''), @p6, @p7, @p8, @p9, @p10, @p11, @p12, 'UNREALISED')",
                id, req.Company, fr.Account, fr.SubledgerType, fr.SubledgerRef, fr.Currency, CobolNumeric.SqlioNumber(fr.ForeignAmount),
                CobolNumeric.SqlioNumber(fr.CarryingGbp), CobolNumeric.SqlioNumber(fr.RateOriginal), CobolNumeric.SqlioNumber(fr.RateAtDate),
                CobolNumeric.SqlioNumber(fr.RevaluedGbp), CobolNumeric.SqlioNumber(fr.UnrealisedGbp));
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
        }
    }

    // 9000-REJECT
    private static void Reject(CaHeader ca)
    {
        ca.ReturnCode = 8;
        ca.MsgCount++;
    }

    // 9300-SQL-FAILED
    private static void SqlFailed(CaHeader ca, SqlioException ex)
    {
        ca.ReasonCode = "SQL0001";
        var text = CobolText.Fit(ex.DriverText, 512)[..200];
        ca.Message = CobolText.IsSpaces(text) ? "SQL failure in " + Program : text;
        ca.ReturnCode = 12;
        ca.MsgCount++;
    }
}
