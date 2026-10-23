using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// POSUPCHK (cobol/online/po/POSUPCHK.cob) CHECK / ENQUIRE: supplier due diligence - the latest evidence per type
/// (po.SupplierDiligence), the D-29 mandatory set (SANCTIONS, COMPANIESHSE, VATNUMBER, BUSINESSSIZE) and the worst-result
/// verdict. RECORD and EXCEPT have no production caller (spec 12.2, SME-015) and are not delivered. Reads only.
/// </summary>
internal sealed class PosupchkProgram(ISqlRunner runner)
{
    private static readonly string[] Mandatory = ["SANCTIONS", "COMPANIESHSE", "VATNUMBER", "BUSINESSSIZE"];

    private readonly PoSql _sql = new(runner);

    public async Task RunAsync(CaHeader ca, PosupchkArea dd, CancellationToken ct)
    {
        if (Init(ca, dd)) return;
        switch (ca.Function)
        {
            case "CHECK" or "ENQUIRE":
                await CheckAsync(ca, dd, ct);
                break;
            case "RECORD" or "EXCEPT" or "EXCEPTIO":
                throw new NotSupportedException($"POSUPCHK {ca.Function} has no production caller and is not delivered (STERP-PO 12.2).");
            default:
                ca.ReasonCode = "DIL0008";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by POSUPCHK");
                ca.ReturnCode = 8;
                break;
        }
    }

    private static bool Init(CaHeader ca, PosupchkArea dd)
    {
        ca.Program = "POSUPCHK";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        dd.FailCnt = 0; dd.ReviewCnt = 0; dd.ExpiredCnt = 0;
        dd.MissingCnt = 0; dd.ExceptedCnt = 0; dd.HeldCnt = 0;
        dd.DetailCnt = 0; dd.DiligenceId = 0; dd.ApprovalId = 0;
        dd.NextReviewDue = 0;
        dd.MissingList = ""; dd.WorstType = "";
        dd.Overall = "PASS";
        dd.CanOrder = "Y";
        if (dd.CheckDate == 0) dd.CheckDate = ca.BusinessDate;
        if (Cob.Spaces(ca.Company) || Cob.Spaces(dd.Supplier))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "DIL0001";
            ca.Message = Cob.Msg("A company code and a supplier code are needed for a due diligence check");
            dd.CanOrder = "N";
            return true;
        }
        return false;
    }

    private async Task CheckAsync(CaHeader ca, PosupchkArea dd, CancellationToken ct)
    {
        if (!await ReadEvidenceAsync(ca, dd, ct)) return;
        if (dd.HeldCnt == 0)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "DIL0002";
            dd.Overall = "EXCEPTION";
            dd.CanOrder = "N";
            dd.WorstType = Mandatory[0];
            ca.Message = Cob.Msg("No due diligence evidence is held for supplier ", Cob.Trim(dd.Supplier), " - onboarding is not complete");
            foreach (var m in Mandatory)
            {
                dd.MissingCnt = Cnt(dd.MissingCnt + 1);
                AppendMissing(dd, m);
            }
            return;
        }
        JudgeHeld(dd);
        JudgeMandatory(dd);
        Verdict(ca, dd);
    }

    /// <summary>2100-READ-EVIDENCE: the latest CheckedAt per evidence type (at most 8 rows).</summary>
    private async Task<bool> ReadEvidenceAsync(CaHeader ca, PosupchkArea dd, CancellationToken ct)
    {
        var r = await _sql.Fetch(
            "SELECT d.EvidenceType, d.Outcome, ISNULL(CONVERT(varchar(8), d.ValidTo, 112), '00000000'), CASE WHEN " +
            "d.IsBlocking = 1 THEN 'Y' ELSE 'N' END, CASE WHEN d.IsMandatory = 1 THEN 'Y' ELSE 'N' END, CASE WHEN " +
            "d.ExceptionBy IS NULL THEN 'N' ELSE 'Y' END, ISNULL(CONVERT(varchar(8), d.NextReviewDate, 112), '00000000') FROM " +
            "po.SupplierDiligence d JOIN (SELECT EvidenceType, MAX(CheckedAt) AS MaxAt FROM po.SupplierDiligence WHERE " +
            "CompanyCode = ? AND SupplierCode = ? GROUP BY EvidenceType) x ON x.EvidenceType = d.EvidenceType AND x.MaxAt = " +
            "d.CheckedAt WHERE d.CompanyCode = ? AND d.SupplierCode = ? ORDER BY d.EvidenceType",
            [PoSql.Tx(ca.Company), PoSql.Tx(dd.Supplier), PoSql.Tx(ca.Company), PoSql.Tx(dd.Supplier)],
            [20, 12, 8, 1, 1, 1, 8], ct);
        if (r.Error)
        {
            ca.ReturnCode = 12;
            ca.ReasonCode = "SQL0001";
            ca.Message = r.Err200;
            dd.CanOrder = "N";
            return false;
        }
        foreach (var row in r.Rows)
        {
            if (dd.DetailCnt >= 8) break;
            dd.DetailCnt++;
            dd.HeldCnt = Cnt(dd.HeldCnt + 1);
            var d = dd.Details[dd.DetailCnt - 1];
            d.DType = row.Text(0);
            d.DResult = row.Text(1);
            d.DMandatory = row.Text(4);
            d.DExcepted = row.Text(5);
            d.DValidTo = 0;
            if (IsNumeric(row.Raw(2))) d.DValidTo = int.Parse(row.Raw(2), System.Globalization.CultureInfo.InvariantCulture);
            if (IsNumeric(row.Raw(6)))
            {
                var review = int.Parse(row.Raw(6), System.Globalization.CultureInfo.InvariantCulture);
                if (review > 0 && (dd.NextReviewDue == 0 || review < dd.NextReviewDue)) dd.NextReviewDue = review;
            }
        }
        return true;
    }

    /// <summary>2200-JUDGE-HELD: an exception forgives expiry; a mandatory PASS past ValidTo is EXPIRED.</summary>
    private static void JudgeHeld(PosupchkArea dd)
    {
        for (var i = 0; i < dd.DetailCnt; i++)
        {
            var d = dd.Details[i];
            var validTo = d.DValidTo;
            if (d.DExcepted == "Y") dd.ExceptedCnt = Cnt(dd.ExceptedCnt + 1);
            switch (d.DResult)
            {
                case "FAIL":
                    dd.FailCnt = Cnt(dd.FailCnt + 1);
                    if (Cob.Spaces(dd.WorstType)) dd.WorstType = d.DType;
                    break;
                case "NOTHELD":
                    if (d.DMandatory == "Y" && d.DExcepted != "Y")
                    {
                        dd.MissingCnt = Cnt(dd.MissingCnt + 1);
                        AppendMissing(dd, d.DType);
                        if (Cob.Spaces(dd.WorstType)) dd.WorstType = d.DType;
                    }
                    break;
                case "EXPIRED":
                    if (d.DExcepted != "Y")
                    {
                        dd.ExpiredCnt = Cnt(dd.ExpiredCnt + 1);
                        if (Cob.Spaces(dd.WorstType)) dd.WorstType = d.DType;
                    }
                    break;
                case "REVIEW":
                    dd.ReviewCnt = Cnt(dd.ReviewCnt + 1);
                    break;
            }
            if (d.DResult == "PASS" && validTo > 0 && validTo < dd.CheckDate && d.DMandatory == "Y" && d.DExcepted != "Y")
            {
                dd.ExpiredCnt = Cnt(dd.ExpiredCnt + 1);
                d.DResult = "EXPIRED";
                if (Cob.Spaces(dd.WorstType)) dd.WorstType = d.DType;
            }
        }
    }

    /// <summary>2300-JUDGE-MANDATORY: the baseline set must be present even when no row was ever created.</summary>
    private static void JudgeMandatory(PosupchkArea dd)
    {
        foreach (var m in Mandatory)
        {
            var hit = false;
            for (var i = 0; i < dd.DetailCnt; i++)
            {
                if (Cob.X(dd.Details[i].DType, 20) == m && dd.Details[i].DResult != "NOTHELD") hit = true;
            }
            if (!hit)
            {
                dd.MissingCnt = Cnt(dd.MissingCnt + 1);
                AppendMissing(dd, m);
                if (Cob.Spaces(dd.WorstType)) dd.WorstType = m;
            }
        }
    }

    /// <summary>2470-APPEND-MISSING: comma list, stops growing once it reaches 170 characters.</summary>
    private static void AppendMissing(PosupchkArea dd, string type)
    {
        if (Cob.Spaces(dd.MissingList))
        {
            dd.MissingList = Cob.X(Cob.Trim(type), 200);
        }
        else if (Cob.Trim(dd.MissingList).Length < 170)
        {
            dd.MissingList = Cob.Str(200, Cob.Trim(dd.MissingList), ", ", Cob.Trim(type));
        }
    }

    /// <summary>2500-VERDICT: the worst result wins.</summary>
    private static void Verdict(CaHeader ca, PosupchkArea dd)
    {
        if (dd.FailCnt > 0)
        {
            dd.Overall = "BLOCKED";
            dd.CanOrder = "N";
            ca.ReturnCode = 8;
            ca.ReasonCode = "DIL0003";
            ca.Message = Cob.Msg("Supplier ", Cob.Trim(dd.Supplier), " has a blocking screening result (", Cob.Trim(dd.WorstType),
                ") and no order may be issued to them");
        }
        else if (dd.ExpiredCnt > 0)
        {
            dd.Overall = "EXCEPTION";
            dd.CanOrder = "N";
            ca.ReturnCode = 6;
            ca.ReasonCode = "DIL0004";
            ca.Message = Cob.Msg("Mandatory due diligence evidence has expired (", Cob.Ed(dd.ExpiredCnt, "Z(3)9"), " item(s), worst ",
                Cob.Trim(dd.WorstType), ") - an authorised exception is required");
        }
        else if (dd.MissingCnt > 0)
        {
            dd.Overall = "EXCEPTION";
            dd.CanOrder = "N";
            ca.ReturnCode = 6;
            ca.ReasonCode = "DIL0005";
            ca.Message = Cob.Msg("Mandatory due diligence evidence has never been held: ", Cob.Trim(dd.MissingList));
        }
        else if (dd.ReviewCnt > 0)
        {
            dd.Overall = "REVIEW";
            dd.CanOrder = "Y";
            ca.ReturnCode = 4;
            ca.ReasonCode = "DIL0006";
            ca.Message = Cob.Msg(Cob.Ed(dd.ReviewCnt, "Z(3)9"), " due diligence result(s) for supplier ", Cob.Trim(dd.Supplier),
                " need review before the next order");
        }
        else
        {
            dd.Overall = "PASS";
            dd.CanOrder = "Y";
            ca.ReturnCode = 0;
        }
    }

    private static int Cnt(int v) => v % 10000;

    private static bool IsNumeric(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');
}
