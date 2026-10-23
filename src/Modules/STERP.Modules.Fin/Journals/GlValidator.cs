using System.Text.Json.Nodes;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Journals;

/// <summary>GLVALIDCA body (cobol/copy/GLVALIDCA.cpy): request fields and the answer GLVALID returns.</summary>
public sealed class GlValidArea
{
    public string Account { get; set; } = "";
    public int PostingDate { get; set; }
    public string DimCc { get; set; } = "";
    public string DimProj { get; set; } = "";
    public string EventCode { get; set; } = "";
    public string SourceModule { get; set; } = "";
    public string VatCode { get; set; } = "";
    public string DrCr { get; set; } = "";
    public decimal Amount { get; set; }
    public string ManualEntry { get; set; } = "";
    public int LineNum { get; set; }

    public string AccountName { get; set; } = "";
    public string ClassCode { get; set; } = "";
    public string ClassType { get; set; } = "";
    public string NormalBalance { get; set; } = "";
    public string Statement { get; set; } = "";
    public string AccountKind { get; set; } = "";
    public string AccountStatus { get; set; } = "";
    public string ControlType { get; set; } = "";
    public string ControlSubledger { get; set; } = "";
    public string AllowManual { get; set; } = "N";
    public string VatEligible { get; set; } = "N";
    public string StatLine { get; set; } = "";
    public string MgmtGroup { get; set; } = "";
    public string TaxAnalysis { get; set; } = "";
    public string CcRequirement { get; set; } = "";
    public string ProjRequired { get; set; } = "";
    public string CcName { get; set; } = "";
    public string ProjName { get; set; } = "";
    public string Valid { get; set; } = "N";
    public string FailField { get; set; } = "";

    /// <summary>The FinJournalLineValidation data object (field order of the copybook).</summary>
    public JsonObject ToJson() => new()
    {
        ["account"] = Account,
        ["accountName"] = AccountName,
        ["classCode"] = ClassCode,
        ["classType"] = ClassType,
        ["normalBalance"] = NormalBalance,
        ["statement"] = Statement,
        ["accountKind"] = AccountKind,
        ["accountStatus"] = AccountStatus,
        ["controlType"] = ControlType,
        ["controlSubledger"] = ControlSubledger,
        ["allowManual"] = AllowManual,
        ["vatEligible"] = VatEligible,
        ["statLine"] = StatLine,
        ["mgmtGroup"] = MgmtGroup,
        ["taxAnalysis"] = TaxAnalysis,
        ["ccRequirement"] = CcRequirement,
        ["projRequired"] = ProjRequired,
        ["ccName"] = CcName,
        ["projName"] = ProjName,
        ["valid"] = Valid,
        ["failField"] = FailField,
    };
}

/// <summary>
/// GLVALID (cobol/online/fin/GLVALID.cob) functions LINE and ACCOUNT - the chart of accounts and analysis dimension check
/// every journal line passes. Only SELECTs, in autocommit on the caller's engine connection.
/// </summary>
public sealed class GlValidator(ISqlRunner sql)
{
    private const string Program = "GLVALID";

    public async Task<CaHeader> RunAsync(CaRequest req, GlValidArea gv, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-002");
        Initialise(req, gv);
        if (!Edit(req, ca, gv)) return ca;
        try
        {
            switch (req.Function)
            {
                case "ACCOUNT":
                    await CheckAccountAsync(req, ca, gv, ct);
                    break;
                case "LINE":
                    await CheckLineAsync(req, ca, gv, ct);
                    break;
                default:
                    gv.Valid = "N";
                    ca.Reject("GLV0009", "GLVALID does not support function " + req.Function.Trim());
                    break;
            }
        }
        catch (SqlioException ex)
        {
            gv.Valid = "N";
            ca.SqlFailed(ex, Program);
        }
        return ca;
    }

    // 0100-INITIALISE
    private static void Initialise(CaRequest req, GlValidArea gv)
    {
        gv.AccountName = gv.ClassCode = gv.ClassType = gv.NormalBalance = gv.Statement = gv.AccountKind = "";
        gv.AccountStatus = gv.ControlType = gv.ControlSubledger = gv.StatLine = gv.MgmtGroup = gv.TaxAnalysis = "";
        gv.CcRequirement = gv.ProjRequired = gv.CcName = gv.ProjName = gv.FailField = "";
        gv.AllowManual = gv.VatEligible = gv.Valid = "N";
        gv.Account = CobolText.Upper(gv.Account);
        gv.DimCc = CobolText.Upper(gv.DimCc);
        gv.DimProj = CobolText.Upper(gv.DimProj);
        gv.EventCode = CobolText.Upper(gv.EventCode);
        if (gv.PostingDate == 0) gv.PostingDate = req.BusinessDate;
    }

    private static bool Blank(string s) => CobolText.IsSpaces(s) || s.All(c => c == '\0');

    // 0200-EDIT-REQUEST
    private static bool Edit(CaRequest req, CaHeader ca, GlValidArea gv)
    {
        if (Blank(req.Company))
        {
            gv.FailField = "CA-COMPANY";
            Reject(ca, gv, "GLV0001", "A company is required to validate an account");
            return false;
        }
        if (req.Function == "EVENT") return true;
        if (Blank(gv.Account))
        {
            gv.FailField = "GV-ACCOUNT";
            Reject(ca, gv, "GLV0002", "An account code is required");
            return false;
        }
        return true;
    }

    private static void Reject(CaHeader ca, GlValidArea gv, string reason, string message)
    {
        gv.Valid = "N";
        ca.Reject(reason, message);
    }

    // 1000-CHECK-ACCOUNT
    private async Task CheckAccountAsync(CaRequest req, CaHeader ca, GlValidArea gv, CancellationToken ct)
    {
        await ReadAccountAsync(req, ca, gv, ct);
        if (ca.Failed) return;
        TestEligibility(ca, gv);
    }

    // 1100-READ-ACCOUNT
    private async Task ReadAccountAsync(CaRequest req, CaHeader ca, GlValidArea gv, CancellationToken ct)
    {
        var row = await sql.SingleAsync(
            "SELECT a.Name, a.ClassCode, c.ClassType, c.NormalBalance, c.Statement, a.AccountKind, a.Status, " +
            "ISNULL(a.ControlType,''), ISNULL(a.ControlSubledger,''), CAST(a.AllowManualJournal AS char(1)), " +
            "CAST(a.IsVatEligible AS char(1)), ISNULL(a.StatutoryLineCode,''), ISNULL(a.ManagementGroup,''), " +
            "ISNULL(a.TaxAnalysisFlag,''), CONVERT(char(8), a.ValidFrom, 112), ISNULL(CONVERT(char(8), a.ValidTo, 112),'') " +
            "FROM fin.Account a JOIN fin.AccountClass c ON c.ClassCode = a.ClassCode WHERE a.CompanyCode = @p1 AND a.AccountCode = @p2",
            req.Company, gv.Account);
        if (row is null)
        {
            gv.FailField = "GV-ACCOUNT";
            ca.Set(16, "GLV0003", "Account " + gv.Account.Trim() + " is not held in the chart of accounts for " + req.Company.Trim());
            return;
        }
        gv.AccountName = Slot.Text(row[0], 80);
        gv.ClassCode = Slot.Text(row[1], 10);
        gv.ClassType = Slot.Text(row[2], 10);
        gv.NormalBalance = Slot.Text(row[3], 2);
        gv.Statement = Slot.Text(row[4], 2);
        gv.AccountKind = Slot.Text(row[5], 8);
        gv.AccountStatus = Slot.Text(row[6], 10);
        gv.ControlType = Slot.Text(row[7], 10);
        gv.ControlSubledger = Slot.Text(row[8], 6);
        gv.StatLine = Slot.Text(row[11], 20);
        gv.MgmtGroup = Slot.Text(row[12], 30);
        gv.TaxAnalysis = Slot.Text(row[13], 20);
        gv.AllowManual = Slot.Text(row[9], 4).StartsWith('1') ? "Y" : "N";
        gv.VatEligible = Slot.Text(row[10], 4).StartsWith('1') ? "Y" : "N";
        _validFrom = CobolText.Slot(row[14], 8);
        _validTo = CobolText.Slot(row[15], 8);
    }

    private string _validFrom = new(' ', 8);
    private string _validTo = new(' ', 8);

    // 1200-TEST-ELIGIBILITY
    private void TestEligibility(CaHeader ca, GlValidArea gv)
    {
        var dateText = gv.PostingDate.ToString("00000000", System.Globalization.CultureInfo.InvariantCulture);
        if (gv.AccountKind != "POSTING")
        {
            gv.FailField = "GV-ACCOUNT";
            Reject(ca, gv, "GLV0004", "Account " + gv.Account.Trim() + " is a summary reporting heading, not a posting account");
            return;
        }
        if (gv.AccountStatus != "ACTIVE")
        {
            gv.FailField = "GV-ACCOUNT";
            Reject(ca, gv, "GLV0005", "Account " + gv.Account.Trim() + " is " + gv.AccountStatus.ToLowerInvariant().Trim() + " and may not be posted to");
            return;
        }
        if (string.CompareOrdinal(_validFrom, dateText) > 0)
        {
            gv.FailField = "GV-POSTING-DATE";
            Reject(ca, gv, "GLV0006", "The account is not yet valid on that posting date");
            return;
        }
        if (!CobolText.IsSpaces(_validTo) && string.CompareOrdinal(_validTo, dateText) < 0)
        {
            gv.FailField = "GV-POSTING-DATE";
            Reject(ca, gv, "GLV0006", "The account was retired before that posting d");
            return;
        }
        if (gv.ManualEntry == "Y" && gv.AllowManual == "N")
        {
            gv.FailField = "GV-ACCOUNT";
            Reject(ca, gv, "GLV0007", "Account " + gv.Account.Trim() + " is a control account for " + gv.ControlSubledger.Trim() +
                " and only that subledger may post to it");
            return;
        }
        gv.Valid = "Y";
    }

    // 2000-CHECK-LINE
    private async Task CheckLineAsync(CaRequest req, CaHeader ca, GlValidArea gv, CancellationToken ct)
    {
        await CheckAccountAsync(req, ca, gv, ct);
        if (ca.Failed) return;
        gv.Valid = "N";
        gv.CcRequirement = await ReadDimRuleAsync(req, gv, "CC", ct);
        await TestCostCentreAsync(req, ca, gv, ct);
        if (ca.Failed) return;
        gv.ProjRequired = await ReadDimRuleAsync(req, gv, "PROJ", ct);
        await TestProjectAsync(req, ca, gv, ct);
        if (ca.Failed) return;
        gv.Valid = "Y";
    }

    // 2100-READ-DIM-RULE
    private async Task<string> ReadDimRuleAsync(CaRequest req, GlValidArea gv, string dimension, CancellationToken ct)
    {
        var row = await sql.SingleAsync(
            "SELECT r.Requirement, ISNULL(r.AllowedValues,'') FROM fin.AccountDimRule r WHERE r.CompanyCode = @p1 " +
            "AND r.AccountCode = @p2 AND r.DimensionCode = @p3",
            req.Company, gv.Account, dimension);
        return row is null ? "OPTIONAL" : Slot.Text(row[0], 10);
    }

    // 2200-TEST-COST-CENTRE
    private async Task TestCostCentreAsync(CaRequest req, CaHeader ca, GlValidArea gv, CancellationToken ct)
    {
        if (gv.CcRequirement == "MANDATORY" && Blank(gv.DimCc))
        {
            gv.FailField = "GV-DIM-CC";
            Reject(ca, gv, "GLV0010", "A cost centre is mandatory on account " + gv.Account.Trim() + " and none was supplied");
            return;
        }
        if (gv.CcRequirement == "FORBIDDEN" && !CobolText.IsSpaces(gv.DimCc))
        {
            gv.FailField = "GV-DIM-CC";
            Reject(ca, gv, "GLV0011", "Account " + gv.Account.Trim() + " does not carry a cost centre");
            return;
        }
        if (CobolText.IsSpaces(gv.DimCc)) return;
        gv.CcName = gv.DimCc;
        var name = await ReadDimValueAsync(req, "CC", gv.DimCc, ct);
        if (name is null)
        {
            gv.FailField = "GV-DIM-CC";
            gv.CcName = "";
            Reject(ca, gv, "GLV0012", "Cost centre " + gv.DimCc.Trim() + " is not an active analysis code");
            return;
        }
        gv.CcName = name;
    }

    // 2300-TEST-PROJECT
    private async Task TestProjectAsync(CaRequest req, CaHeader ca, GlValidArea gv, CancellationToken ct)
    {
        if (gv.ProjRequired == "MANDATORY" && Blank(gv.DimProj))
        {
            gv.FailField = "GV-DIM-PROJ";
            Reject(ca, gv, "GLV0013", "A project code is mandatory on account " + gv.Account.Trim() + " and none was supplied");
            return;
        }
        if (gv.ProjRequired == "FORBIDDEN" && !CobolText.IsSpaces(gv.DimProj))
        {
            gv.FailField = "GV-DIM-PROJ";
            Reject(ca, gv, "GLV0014", "Account " + gv.Account.Trim() + " does not carry a project code");
            return;
        }
        if (CobolText.IsSpaces(gv.DimProj)) return;
        var name = await ReadDimValueAsync(req, "PROJ", gv.DimProj, ct);
        if (name is null)
        {
            gv.FailField = "GV-DIM-PROJ";
            Reject(ca, gv, "GLV0015", "Project " + gv.DimProj.Trim() + " is not a permitted combination for account " + gv.Account.Trim());
            return;
        }
        gv.ProjName = name;
    }

    // 2400-READ-DIM-VALUE: the name of an active value, or null when not found
    private async Task<string?> ReadDimValueAsync(CaRequest req, string dimension, string value, CancellationToken ct)
    {
        var row = await sql.SingleAsync(
            "SELECT d.Name, CAST(d.IsActive AS char(1)) FROM fin.DimensionValue d WHERE d.CompanyCode = @p1 " +
            "AND d.DimensionCode = @p2 AND d.DimValue = @p3 AND d.IsActive = 1",
            req.Company, dimension, value);
        return row is null ? null : Slot.Text(row[0], 80);
    }
}
