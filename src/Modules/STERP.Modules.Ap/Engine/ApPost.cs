using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ap.Engine;

/// <summary>One PJ-LINE of APPOSTWK.</summary>
public sealed class PostingLine
{
    public string Account { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string CostCentre { get; set; } = "";
    public string Project { get; set; } = "";
    public string VatCode { get; set; } = "";
    public string Job { get; set; } = "";
    public string Bank { get; set; } = "";
    public string Narrative { get; set; } = "";
}

/// <summary>WS-APPOST (cobol/copy/APPOSTWK.cpy): APPOST's own header and the journal request/answer.</summary>
public sealed class PostingArea
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
    public int LineCount { get; set; }
    public List<PostingLine> Lines { get; } = [];
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
/// APPOST (cobol/online/ap/APPOST.cob:72-382; FR-STERP-AP-052, spec 4.14): the purchase ledger's route into the nominal ledger
/// over the PLATFORM GL posting gateway - fin.JournalStage rows under the caller's correlation id (cleared first), then
/// fin.usp_PostJournal with the 13 positional parameters and SourceModule 'AP'. The staging statements autocommit and the
/// procedure owns its transaction; a repeated correlation id answers RC 4 APP0007 with the original journal.
/// </summary>
public sealed class ApPost(IGlPostingGateway gl)
{
    private bool _fatal;
    private decimal _totDr;
    private decimal _totCr;

    public async Task CallAsync(PostingArea pj, CancellationToken ct = default)
    {
        Init(pj);
        if (_fatal) return;
        switch (pj.Func)
        {
            case "STAGE":
                await StageAsync(pj, ct);
                break;
            case "POST":
                await StageAsync(pj, ct);
                if (!_fatal) await PostAsync(pj, ct);
                break;
            case "CLEAR":
                await ClearAsync(pj, ct);
                break;
            default:
                pj.Reason = "APP0099";
                pj.Message = CobolText.Trimmed("APPOST does not support function " + pj.Func.Trim(), 200);
                Reject(pj);
                break;
        }
    }

    // 1000-INIT
    private void Init(PostingArea pj)
    {
        _fatal = false;
        pj.Rc = 0;
        pj.Reason = "";
        pj.Message = "";
        pj.WasDuplicate = "N";
        pj.Balanced = "N";
        _totDr = 0;
        _totCr = 0;
        if (CobolText.IsSpaces(pj.Currency)) pj.Currency = "GBP";
        if (pj.FxRate == 0) pj.FxRate = 1;
        if (CobolText.IsSpaces(pj.JournalType)) pj.JournalType = "AP";
        if (CobolText.IsSpaces(pj.SeriesCode)) pj.SeriesCode = "JNL";
        if (CobolText.IsSpaces(pj.Correlation))
        {
            pj.Reason = "APP0001";
            pj.Message = "A correlation id is required so that a retried posting cannot post twice";
            Reject(pj);
            return;
        }
        if (pj.Func != "CLEAR" && pj.LineCount == 0)
        {
            pj.Reason = "APP0002";
            pj.Message = "A journal must have lines";
            Reject(pj);
        }
        if (pj.LineCount > 30) pj.LineCount = 30;
    }

    // 2000-STAGE: any residue of a previous attempt under this correlation id is cleared first
    private async Task StageAsync(PostingArea pj, CancellationToken ct)
    {
        await ClearAsync(pj, ct);
        if (_fatal) return;
        for (var i = 1; i <= pj.LineCount && !_fatal; i++) await StageLineAsync(pj, i, ct);
        if (_fatal) return;
        if (_totDr == _totCr)
        {
            pj.Balanced = "Y";
        }
        else
        {
            pj.Balanced = "N";
            var diff = CobolNumeric.Store(_totDr - _totCr, 15, 6);
            pj.Reason = "APP0003";
            pj.Message = CobolText.Trimmed("The purchase ledger journal does not balance; debits exceed credits by " +
                CobolEdit.Field(diff, "-(11)9.99").Trim(), 200);
            Reject(pj);
            await ClearAsync(pj, ct);
            return;
        }
        pj.TotalDrGbp = _totDr;
        pj.TotalCrGbp = _totCr;
        pj.Message = "Journal lines staged";
    }

    // 2100-ONE-LINE
    private async Task StageLineAsync(PostingArea pj, int i, CancellationToken ct)
    {
        var l = i <= pj.Lines.Count ? pj.Lines[i - 1] : new PostingLine();
        if (CobolText.IsSpaces(l.Account))
        {
            pj.Reason = "APP0004";
            pj.Message = "Every journal line needs an account code";
            Reject(pj);
            return;
        }
        _totDr = CobolNumeric.Store(_totDr + l.Debit, 13, 2);
        _totCr = CobolNumeric.Store(_totCr + l.Credit, 13, 2);
        var line = new JournalStageLine(CobolText.Trimmed(pj.Correlation, 64), i)
            .Text("CompanyCode", CobolText.Trimmed(pj.Company, 10))
            .Text("AccountCode", CobolText.Trimmed(l.Account, 12))
            .Amount("DebitAmount", l.Debit)
            .Amount("CreditAmount", l.Credit)
            .Text("CurrencyCode", CobolText.Trimmed(pj.Currency, 3))
            .NullIfBlank("DimCostCentre", CobolText.Trimmed(l.CostCentre, 20))
            .NullIfBlank("DimProject", CobolText.Trimmed(l.Project, 20))
            .NullIfBlank("Narrative", CobolText.Trimmed(l.Narrative, 60))
            .NullIfBlank("VatCode", CobolText.Trimmed(l.VatCode, 4))
            .NullIfBlank("JobRef", CobolText.Trimmed(l.Job, 20))
            .NullIfBlank("SubledgerType", CobolText.Trimmed(pj.SubledgerType, 6))
            .NullIfBlank("SubledgerRef", CobolText.Trimmed(pj.SubledgerRef, 40))
            .NullIfBlank("SourceRef", CobolText.Trimmed(pj.SourceRef, 40))
            .NullIfBlank("BankAccountCode", CobolText.Trimmed(l.Bank, 20))
            .Text("CreatedBy", CobolText.Trimmed(pj.User, 20));
        var r = await gl.StageLineAsync(line, ct);
        if (r.Failed) SqlFailed(pj, r.ErrorText);
    }

    // 3000-POST: fin.usp_PostJournal validates and owns the transaction
    private async Task PostAsync(PostingArea pj, CancellationToken ct)
    {
        if (pj.DocumentDate == 0) pj.DocumentDate = pj.PostingDate;
        var request = new GlPostRequest
        {
            CompanyCode = CobolText.Trimmed(pj.Company, 10),
            CorrelationId = CobolText.Trimmed(pj.Correlation, 64),
            UserCode = CobolText.Trimmed(pj.User, 20),
            Description = CobolText.Trimmed(pj.Description, 200),
            PostingDate = pj.PostingDate,
            JournalType = CobolText.Trimmed(pj.JournalType, 12),
            DocumentDate = pj.DocumentDate,
            CurrencyCode = CobolText.Trimmed(pj.Currency, 3),
            SourceModule = "AP",
            SourceRef = CobolText.Trimmed(pj.SourceRef, 40),
            EventCode = CobolText.Trimmed(pj.EventCode, 30),
            SeriesCode = CobolText.Trimmed(pj.SeriesCode, 20),
            FxRate = CobolNumeric.Store(pj.FxRate, 9, 8),
            EvidenceRef = CobolText.IsSpaces(pj.EvidenceRef) ? null : CobolText.Trimmed(pj.EvidenceRef, 120),
            ArgumentCount = 14,
        };
        var r = await gl.PostAsync(request, [24, 30, 10, 8, 8, 24, 24, 12, 8], ct);
        if (r.Failed)
        {
            _fatal = true;
            pj.Rc = 8;
            pj.Reason = "APP0005";
            pj.Message = CobolText.Trimmed(r.ErrorText, 200);
            if (CobolText.IsSpaces(pj.Message)) pj.Message = "The nominal ledger refused the posting";
            return;
        }
        if (r.NoRow)
        {
            pj.Rc = 12;
            pj.Reason = "APP0006";
            pj.Message = "The nominal ledger returned no posting result";
            _fatal = true;
            return;
        }
        var row = r.Row!;
        pj.JournalId = (long)CobolNumeric.Store(row.NumVal(0), 9, 0);
        pj.JournalNumber = CobolText.Trimmed(row.Raw(1), 30);
        pj.FiscalYear = (int)CobolNumeric.Store(row.NumVal(3), 4, 0, signed: false);
        pj.PeriodNo = (int)CobolNumeric.Store(row.NumVal(4), 2, 0, signed: false);
        pj.TotalDrGbp = CobolNumeric.Store(row.NumVal(5), 13, 2);
        pj.TotalCrGbp = CobolNumeric.Store(row.NumVal(6), 13, 2);
        if (row.Raw(8).StartsWith('1'))
        {
            pj.WasDuplicate = "Y";
            pj.Rc = 4;
            pj.Reason = "APP0007";
            pj.Message = CobolText.Trimmed("This instruction was already posted as " + pj.JournalNumber.Trim() + "; it has not been posted again", 200);
        }
        else
        {
            pj.WasDuplicate = "N";
            pj.Rc = 0;
            pj.Message = CobolText.Trimmed("Posted as journal " + pj.JournalNumber.Trim(), 200);
        }
    }

    // 4000-CLEAR
    private async Task ClearAsync(PostingArea pj, CancellationToken ct)
    {
        var r = await gl.ClearStageAsync(CobolText.Trimmed(pj.Correlation, 64), ct);
        if (r.Failed) SqlFailed(pj, r.ErrorText);
    }

    // 9000-REJECT
    private void Reject(PostingArea pj)
    {
        _fatal = true;
        pj.Rc = 8;
    }

    // 9300-SQL-FAILED
    private void SqlFailed(PostingArea pj, string driverText)
    {
        _fatal = true;
        pj.Rc = 12;
        pj.Reason = "SQL0001";
        pj.Message = CobolText.Trimmed(driverText, 200);
        if (CobolText.IsSpaces(pj.Message)) pj.Message = "SQL failure in APPOST";
    }
}
