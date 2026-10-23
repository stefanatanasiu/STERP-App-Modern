using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Programs;

/// <summary>One PP-LINE entry of POPOSTWK.</summary>
internal sealed class PopostLine
{
    public string Account { get; set; } = "";
    public decimal Dr { get; set; }
    public decimal Cr { get; set; }
    public string CostCtr { get; set; } = "";
    public string Project { get; set; } = "";
    public string VatCode { get; set; } = "";
    public string Job { get; set; } = "";
    public string Narrative { get; set; } = "";
}

/// <summary>WS-POPOST (cobol/copy/POPOSTWK.cpy, prefix PP-).</summary>
internal sealed class PopostArea
{
    public string Func { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public int BusinessDate { get; set; }
    public string Correlation { get; set; } = "";
    public int Rc { get; set; }
    public string Reason { get; set; } = "";
    public string Message { get; set; } = "";
    public string EventCode { get; set; } = "";
    public string JournalType { get; set; } = "";
    public string SourceRef { get; set; } = "";
    public string Description { get; set; } = "";
    public int PostingDate { get; set; }
    public int DocumentDate { get; set; }
    public string Currency { get; set; } = "";
    public decimal FxRate { get; set; }
    public string SeriesCode { get; set; } = "";
    public string EvidenceRef { get; set; } = "";
    public string SubledgerType { get; set; } = "";
    public string SubledgerRef { get; set; } = "";
    public List<PopostLine> Lines { get; } = [];
    public string JournalNumber { get; set; } = "";
    public long JournalId { get; set; }
    public int FiscalYear { get; set; }
    public int PeriodNo { get; set; }
    public decimal TotalDrGbp { get; set; }
    public decimal TotalCrGbp { get; set; }
    public string WasDuplicate { get; set; } = "";
    public string Balanced { get; set; } = "";
}

/// <summary>
/// POPOST (cobol/online/po/POPOST.cob): the purchasing route to the nominal ledger through the PLATFORM GL posting gateway -
/// STAGE (clear, fin.JournalStage rows, balance check), POST (STAGE + fin.usp_PostJournal), CLEAR. Runs on the caller's
/// unit of work (inside RECEIPT's transaction, or autocommit for INSPECT).
/// </summary>
internal sealed class PopostProgram(IGlPostingGateway gl)
{
    public async Task RunAsync(PopostArea pp, CancellationToken ct)
    {
        var fatal = Init(pp);
        if (fatal) return;
        switch (pp.Func)
        {
            case "STAGE":
                await StageAsync(pp, ct);
                break;
            case "POST":
                if (await StageAsync(pp, ct)) await PostAsync(pp, ct);
                break;
            case "CLEAR":
                await ClearAsync(pp, ct);
                break;
            default:
                pp.Rc = 8;
                pp.Reason = "POP0009";
                pp.Message = Cob.Msg("Function ", Cob.Trim(pp.Func), " is not supported by POPOST");
                break;
        }
    }

    private static bool Init(PopostArea pp)
    {
        pp.Rc = 0;
        pp.Reason = "";
        pp.Message = "";
        pp.WasDuplicate = "N";
        pp.Balanced = "N";
        if (Cob.Spaces(pp.Currency)) pp.Currency = "GBP";
        if (pp.FxRate == 0) pp.FxRate = 1;
        if (Cob.Spaces(pp.JournalType)) pp.JournalType = "PO";
        if (Cob.Spaces(pp.SeriesCode)) pp.SeriesCode = "JNL";
        if (Cob.Spaces(pp.Correlation))
        {
            pp.Reason = "POP0001";
            pp.Message = Cob.Msg("A correlation id is required so that a retried goods receipt cannot accrue twice");
            pp.Rc = 8;
            return true;
        }
        var fatal = false;
        if (pp.Func != "CLEAR" && pp.Lines.Count == 0)
        {
            pp.Reason = "POP0002";
            pp.Message = "A journal must have lines";
            pp.Rc = 8;
            fatal = true;
        }
        if (pp.Lines.Count > 30) pp.Lines.RemoveRange(30, pp.Lines.Count - 30);
        return fatal;
    }

    /// <summary>2000-STAGE: residue of an earlier attempt is cleared first, then one row per line.</summary>
    private async Task<bool> StageAsync(PopostArea pp, CancellationToken ct)
    {
        if (!await ClearAsync(pp, ct)) return false;
        decimal totDr = 0, totCr = 0;
        for (var i = 0; i < pp.Lines.Count; i++)
        {
            var l = pp.Lines[i];
            if (Cob.Spaces(l.Account))
            {
                pp.Reason = "POP0004";
                pp.Message = Cob.Msg("Every journal line needs an account code");
                pp.Rc = 8;
                return false;
            }
            totDr = Cob.T(totDr + l.Dr, 13, 2);
            totCr = Cob.T(totCr + l.Cr, 13, 2);
            var r = await gl.StageStatementAsync(
                "INSERT fin.JournalStage (CorrelationId, LineNum, CompanyCode, AccountCode, DebitAmount, CreditAmount, CurrencyCode, " +
                "DimCostCentre, DimProject, Narrative, VatCode, JobRef, SubledgerType, SubledgerRef, SourceRef, CreatedBy) VALUES (?, " +
                "?, ?, ?, ?, ?, ?, NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), " +
                "NULLIF(?,''), ?)",
                [PoSql.Tx(pp.Correlation), PoSql.Int(i + 1), PoSql.Tx(pp.Company), PoSql.Tx(l.Account), PoSql.Num(l.Dr),
                 PoSql.Num(l.Cr), PoSql.Tx(pp.Currency), PoSql.Tx(l.CostCtr), PoSql.Tx(l.Project), PoSql.Tx(l.Narrative),
                 PoSql.Tx(l.VatCode), PoSql.Tx(l.Job), PoSql.Tx(pp.SubledgerType), PoSql.Tx(pp.SubledgerRef), PoSql.Tx(pp.SourceRef),
                 PoSql.Tx(pp.User)], ct);
            if (r.Failed)
            {
                pp.Rc = 12;
                pp.Reason = "POP0008";
                pp.Message = Cob.X(r.ErrorText, 200);
                return false;
            }
        }
        if (totDr == totCr)
        {
            pp.Balanced = "Y";
        }
        else
        {
            pp.Balanced = "N";
            pp.Reason = "POP0003";
            pp.Message = Cob.Msg("The purchasing journal does not balance; debits exceed credits by ",
                Cob.Amt(Cob.T(totDr - totCr, 15, 6)));
            pp.Rc = 8;
            await ClearAsync(pp, ct);
            return false;
        }
        pp.TotalDrGbp = totDr;
        pp.TotalCrGbp = totCr;
        pp.Message = "Journal lines staged";
        return true;
    }

    /// <summary>3000-POST: fin.usp_PostJournal owns the validation and the transaction.</summary>
    private async Task PostAsync(PopostArea pp, CancellationToken ct)
    {
        if (pp.DocumentDate == 0) pp.DocumentDate = pp.PostingDate;
        var post = await gl.PostAsync(new GlPostRequest
        {
            CompanyCode = pp.Company,
            CorrelationId = pp.Correlation,
            UserCode = pp.User,
            Description = pp.Description,
            PostingDate = pp.PostingDate,
            JournalType = pp.JournalType,
            DocumentDate = pp.DocumentDate,
            CurrencyCode = pp.Currency,
            SourceModule = "PO",
            SourceRef = pp.SourceRef,
            EventCode = pp.EventCode,
            SeriesCode = pp.SeriesCode,
            FxRate = pp.FxRate,
            EvidenceRef = Cob.Spaces(pp.EvidenceRef) ? null : pp.EvidenceRef,
            ArgumentCount = 14,
        }, GlPostingGateway.DefaultWidths, ct);
        if (post.Failed)
        {
            pp.Rc = 8;
            pp.Reason = "POP0005";
            pp.Message = Cob.X(post.ErrorText, 200);
            if (Cob.Spaces(pp.Message)) pp.Message = "The nominal ledger refused the posting";
            return;
        }
        if (post.NoRow)
        {
            pp.Rc = 12;
            pp.Reason = "POP0006";
            pp.Message = Cob.Msg("The nominal ledger returned no posting result");
            return;
        }
        pp.JournalId = post.JournalId;
        pp.JournalNumber = Cob.X(post.JournalNumber, 30);
        pp.FiscalYear = post.FiscalYear % 10000;
        pp.PeriodNo = post.PeriodNo % 100;
        pp.TotalDrGbp = Cob.T(post.TotalDebitGbp, 13, 2);
        pp.TotalCrGbp = Cob.T(post.TotalCreditGbp, 13, 2);
        if (post.WasDuplicate)
        {
            pp.WasDuplicate = "Y";
            pp.Rc = 4;
            pp.Reason = "POP0007";
            pp.Message = Cob.Msg("This instruction was already posted as ", Cob.Trim(pp.JournalNumber), "; it has not been posted again");
        }
        else
        {
            pp.WasDuplicate = "N";
            pp.Rc = 0;
            pp.Message = Cob.Msg("Posted as journal ", Cob.Trim(pp.JournalNumber));
        }
    }

    /// <summary>4000-CLEAR.</summary>
    private async Task<bool> ClearAsync(PopostArea pp, CancellationToken ct)
    {
        var r = await gl.ClearStageAsync(Cob.X(pp.Correlation, 64), ct);
        if (r.Failed)
        {
            pp.Rc = 12;
            pp.Reason = "POP0008";
            pp.Message = Cob.X(r.ErrorText, 200);
            return false;
        }
        return true;
    }
}
