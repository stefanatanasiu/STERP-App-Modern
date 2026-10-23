using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Domain;

/// <summary>
/// SOVAT (cobol/online/so/SOVAT.cob, FR-SOVAT-001..020): the VAT liability decision of a sales line with its rule trace,
/// rate and invoice wording (FR-STERP-SO-013). Reads only (inv.ItemVatClass, fin.VatCode, fin.VatRate, PARMGET).
/// </summary>
public sealed class SovatProgram(ISqlRunner sql, IParameterService parameters)
{
    private const string Program = "SOVAT";

    private const string ItemClassSql =
        "SELECT TOP 1 v.VatCode, ISNULL(v.EvidenceRef,'')" +
        " FROM inv.ItemVatClass v WHERE v.CompanyCode = ?" +
        " AND v.ItemCode = ? AND v.Direction = 'SALE' AND " +
        "v.EffectiveFrom <= CONVERT(date, ?, 112) AND " +
        "(v.EffectiveTo IS NULL OR v.EffectiveTo >= " +
        "CONVERT(date, ?, 112)) ORDER BY v.EffectiveFrom " +
        "DESC";

    private const string VatCodeSql =
        "SELECT TOP 1 c.Liability, c.Mechanism, " +
        "ISNULL(c.InvoiceWording,''), " +
        "ISNULL(c.EvidenceRequired,''), " +
        "(SELECT TOP 1 CONVERT(varchar(20), r.RatePct) " +
        "FROM fin.VatRate r WHERE r.VatCode = c.VatCode " +
        "AND r.EffectiveFrom <= CONVERT(date, ?, 112) " +
        "ORDER BY r.EffectiveFrom DESC) FROM fin.VatCode c" +
        " WHERE c.VatCode = ?";

    private static readonly int[] Widths = Enumerable.Repeat(200, 5).ToArray();

    private sealed class State
    {
        public bool InputBad;
        public bool Decided;
        public string Text = "";
    }

    public async Task RunAsync(SovatArea sv, CancellationToken ct = default)
    {
        var s = new State();
        Initialise(sv);
        Validate(sv, s);
        if (s.InputBad) return;
        switch (sv.Header.Function)
        {
            case "DECIDE":
            case "DETERMIN":
            case "EXPLAIN":
                await DetermineAsync(sv, s, ct);
                break;
            case "EVIDENCE":
                Evidence(sv);
                break;
            case "OVERRIDE":
                await OverrideAsync(sv, s, ct);
                break;
            default:
                sv.Header.ReasonCode = "VAT0008";
                sv.Header.Message = Cob.Str(200, "Function ", Cob.Trim(sv.Header.Function), " is not DECIDE, EXPLAIN, EVIDENCE or OVERRIDE");
                Reject(sv, s);
                break;
        }
    }

    /// <summary>0100-INITIALISE.</summary>
    private static void Initialise(SovatArea sv)
    {
        var h = sv.Header;
        h.Program = Program;
        h.ReturnCode = 0;
        h.MessageCount = 0;
        h.ReasonCode = "";
        h.Message = "";
        h.RequestRef = "SO-025";
        h.Function = Cob.Upper(h.Function, 8);
        sv.LineType = Cob.Upper(sv.LineType, 10);
        sv.CustLocation = Cob.Upper(sv.CustLocation, 3);
        sv.DeliverLocation = Cob.Upper(sv.DeliverLocation, 3);
        sv.ItemVatCode = Cob.Upper(sv.ItemVatCode, 4);
        sv.OverrideCode = Cob.Upper(sv.OverrideCode, 4);
        if (Cob.Spaces(sv.LineType)) sv.LineType = "GOODS";
        if (Cob.Spaces(sv.CustLocation)) sv.CustLocation = "GB";
        if (Cob.Spaces(sv.DeliverLocation)) sv.DeliverLocation = sv.CustLocation;
        if (Cob.Spaces(sv.PlaceOfSupply)) sv.PlaceOfSupply = "GB";
        if (sv.SupplyDate == 0) sv.SupplyDate = h.BusinessDate;
        if (sv.EvidenceMonths < 1 || sv.EvidenceMonths > 12) sv.EvidenceMonths = 3;
        if (Cob.Spaces(sv.Currency)) sv.Currency = "GBP";
        if (sv.NetAmountGbp == 0) sv.NetAmountGbp = sv.NetAmount;
        sv.VatCode = ""; sv.Liability = ""; sv.Mechanism = "";
        sv.InvoiceWording = ""; sv.EvidenceReqd = "";
        sv.BlockReason = ""; sv.BlockText = "";
        sv.TreatmentBasis = "";
        sv.VatRatePct = 0; sv.VatAmount = 0;
        sv.ProofDeadline = 0;
        sv.EvidenceStatus = "NA";
        sv.BlockInvoice = "N"; sv.SelfAccounts = "N";
        sv.DrcApplies = "N"; sv.OverrideApplied = "N";
        sv.RuleCnt = 0;
        foreach (var r in sv.Rule)
        {
            r.RSeq = 0;
            r.RText = "";
        }
    }

    /// <summary>0200-VALIDATE.</summary>
    private static void Validate(SovatArea sv, State s)
    {
        if (Cob.Spaces(sv.Header.Company) || Cob.Spaces(sv.Customer))
        {
            sv.Header.ReasonCode = "VAT0001";
            sv.Header.Message = "A company and a customer are required";
            Reject(sv, s);
            return;
        }
        if (sv.CustLocation is not ("GB" or "XI" or "EU" or "ROW"))
        {
            sv.Header.ReasonCode = "VAT0011";
            sv.Header.Message = Cob.Str(200, "Customer VAT location ", Cob.Trim(sv.CustLocation), " is not one of GB, XI, EU or ROW");
            Reject(sv, s);
            return;
        }
        if (sv.DeliverLocation is not ("GB" or "XI" or "EU" or "ROW"))
        {
            sv.Header.ReasonCode = "VAT0011";
            sv.Header.Message = Cob.Str(200, "Delivery VAT location ", Cob.Trim(sv.DeliverLocation), " is not one of GB, XI, EU or ROW");
            Reject(sv, s);
        }
    }

    /// <summary>1000-DETERMINE: the rules in their fixed order, each firing rule written to the trace.</summary>
    private async Task DetermineAsync(SovatArea sv, State s, CancellationToken ct)
    {
        await ItemClassAsync(sv, s, ct);
        if (s.InputBad) return;
        await DeMinimisAsync(sv, ct);
        NiEuCheck(sv, s);
        if (s.Decided) return;
        DomesticCode(sv, s);
        if (s.Decided) return;
        OverseasService(sv, s);
        if (s.Decided)
        {
            await FinishAsync(sv, s, ct);
            return;
        }
        ExportOfGoods(sv, s);
        if (s.Decided)
        {
            await FinishAsync(sv, s, ct);
            return;
        }
        DomesticReverse(sv, s);
        if (s.Decided)
        {
            await FinishAsync(sv, s, ct);
            return;
        }
        UkSupply(sv, s);
        await FinishAsync(sv, s, ct);
    }

    /// <summary>2000-ITEM-CLASS: the item's sales VAT classification (COMMAREA, else inv.ItemVatClass SALE).</summary>
    private async Task ItemClassAsync(SovatArea sv, State s, CancellationToken ct)
    {
        if (!Cob.Spaces(sv.ItemVatCode))
        {
            sv.ItemVatSource = "COMMAREA";
            return;
        }
        if (sv.ReadMaster != "Y")
        {
            sv.Header.ReasonCode = "VAT0002";
            sv.Header.Message = Cob.Str(200, "No VAT classification was supplied for item ", Cob.Trim(sv.ItemCode));
            Reject(sv, s);
            return;
        }
        SqlioRow row;
        try
        {
            var d = Cob.D8(sv.SupplyDate);
            row = await sql.SingleAsync(ItemClassSql,
                [SqlioParm.Text(sv.Header.Company), SqlioParm.Text(sv.ItemCode), SqlioParm.Text(d), SqlioParm.Text(d)], Widths, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(sv, s, ex);
            return;
        }
        if (!row.Found)
        {
            sv.Header.ReasonCode = "VAT0002";
            sv.Header.Message = Cob.Str(200, "No effective sales VAT classification for ", "item ", Cob.Trim(sv.ItemCode));
            Reject(sv, s);
            return;
        }
        sv.ItemVatCode = Cob.X(row.Raw(0), 4);
        sv.ItemVatSource = "ITEMVATCLASS";
    }

    /// <summary>3000-DE-MINIMIS: SO.VAT.DRC.DEMINIMIS through PARMGET GETNUM (baseline 5000.00).</summary>
    private async Task DeMinimisAsync(SovatArea sv, CancellationToken ct)
    {
        if (sv.DrcDeminimis > 0) return;
        sv.DrcDeminimis = 5000.00m;
        var caller = new KernelCaller(sv.Header.Company, "", "", sv.Header.BusinessDate, "");
        var pm = await parameters.GetAsync(caller, "SO.VAT.DRC.DEMINIMIS", sv.SupplyDate, "5000", numeric: true, ct: ct);
        if (pm.Header.ReturnCode < 8 && pm.ValueNum > 0) sv.DrcDeminimis = Cob.St(pm.ValueNum, 11, 2);
    }

    /// <summary>5000-EVIDENCE: proof of export within the permitted months.</summary>
    private static void Evidence(SovatArea sv)
    {
        var date = sv.DespatchDate != 0 ? sv.DespatchDate : sv.SupplyDate;
        var deadline = AddMonths(date, sv.EvidenceMonths);
        sv.ProofDeadline = deadline;
        sv.EvidenceReqd = Cob.Str(100, "Commercial and official evidence of export ", "within three months of the supply");
        if (sv.ProofObtained == "Y" && !Cob.Spaces(sv.ProofType))
        {
            sv.EvidenceStatus = "OBTAINED";
            return;
        }
        sv.EvidenceStatus = sv.Header.BusinessDate > deadline ? "OVERDUE" : "PENDING";
    }

    /// <summary>5100-ADD-MONTHS: the 1994 month arithmetic with the day clamped to 28.</summary>
    private static int AddMonths(int date, int months)
    {
        var yyyy = date / 10000 % 10000;
        var mm = date / 100 % 100;
        var dd = date % 100;
        mm += months;
        while (mm > 12)
        {
            mm -= 12;
            yyyy = (yyyy + 1) % 10000;
        }
        if (dd > 28) dd = 28;
        return (int)Cob.St(yyyy * 10000m + mm * 100m + dd, 8, 0, false);
    }

    /// <summary>6000-OVERRIDE: the determined treatment first, then the authorised override (RC 04 VAT0007).</summary>
    private async Task OverrideAsync(SovatArea sv, State s, CancellationToken ct)
    {
        if (Cob.Spaces(sv.OverrideCode) || Cob.Spaces(sv.OverrideReason) || Cob.Spaces(sv.OverrideApprovl))
        {
            sv.Header.ReasonCode = "VAT0007";
            sv.Header.Message = Cob.Str(200, "A VAT override needs a code, a written ", "reason and an approval reference");
            Reject(sv, s);
            return;
        }
        await DetermineAsync(sv, s, ct);
        if (s.InputBad) return;
        sv.VatCode = Cob.X(sv.OverrideCode, 4);
        sv.OverrideApplied = "Y";
        sv.TreatmentBasis = "OVERRIDE";
        sv.BlockInvoice = "N";
        sv.BlockReason = "";
        sv.BlockText = "";
        await RateAndWordingAsync(sv, s, ct);
        s.Text = Cob.Str(200, "Authorised override to ", Cob.Trim(sv.OverrideCode), " by ", Cob.Trim(sv.OverrideApprovl), " - ",
            Cob.Trim(sv.OverrideReason));
        AddRule(sv, s);
        await FinishAsync(sv, s, ct);
        if (sv.Header.ReturnCode < 4)
        {
            sv.Header.ReturnCode = 4;
            sv.Header.ReasonCode = "VAT0007";
            sv.Header.MessageCount++;
        }
    }

    /// <summary>7100-NI-EU-CHECK: goods between Northern Ireland and the EU are outside the baseline (D-23).</summary>
    private static void NiEuCheck(SovatArea sv, State s)
    {
        if (sv.LineType != "GOODS") return;
        if ((sv.DeliverLocation == "XI" && sv.CustLocation == "EU") || (sv.DeliverLocation == "EU" && sv.CustLocation == "XI"))
        {
            sv.Header.ReasonCode = "VAT0009";
            sv.Header.Message = Cob.Str(200, "Goods movements between Northern Ireland and", " the EU are outside the baseline (D-23)");
            sv.BlockText = sv.Header.Message;
            sv.BlockReason = "VAT0009";
            sv.BlockInvoice = "Y";
            s.Decided = true;
            Reject(sv, s);
        }
    }

    /// <summary>7150-DOMESTIC-CODE: a domestic code for a delivery outside the UK needs authorised evidence.</summary>
    private static void DomesticCode(SovatArea sv, State s)
    {
        if (sv.LineType != "GOODS") return;
        if (sv.DeliverLocation is "GB" or "XI") return;
        if (sv.OverrideCode is not ("S" or "R" or "Z" or "E")) return;
        if (!Cob.Spaces(sv.OverrideApprovl)) return;
        sv.Header.ReasonCode = "VAT0006";
        sv.BlockReason = "VAT0006";
        sv.BlockInvoice = "Y";
        sv.BlockText = Cob.Str(200, "A domestic VAT code cannot be used for a ", "delivery outside the UK without authorised ", "evidence");
        sv.Header.Message = sv.BlockText;
        s.Text = sv.BlockText;
        AddRule(sv, s);
        s.Decided = true;
        Reject(sv, s);
    }

    /// <summary>7200-OVERSEAS-SERVICE: services to an overseas business customer are outside the scope of UK VAT.</summary>
    private static void OverseasService(SovatArea sv, State s)
    {
        if (sv.LineType != "SERVICE") return;
        if (sv.CustLocation is "GB" or "XI") return;
        if (sv.CustIsBusiness != "Y")
        {
            s.Text = Cob.Str(200, "Services to an overseas consumer stay in the", " UK under the general rule");
            AddRule(sv, s);
            return;
        }
        sv.VatCode = "O";
        sv.Liability = "OUTSIDE";
        sv.Mechanism = "RCSERVICE";
        sv.SelfAccounts = "Y";
        sv.TreatmentBasis = "PLACEOFSUPPLY";
        s.Decided = true;
        s.Text = Cob.Str(200, "Service supplied to a business customer in ", Cob.Trim(sv.CustLocation), " - outside the scope of UK VAT");
        AddRule(sv, s);
        sv.EvidenceReqd = Cob.Str(100, "Evidence that the customer belongs outside the ", "UK and is in business");
        if (Cob.Spaces(sv.CustVatRegNo) && sv.CustLocation == "EU") NoVatNumber(sv, s);
    }

    /// <summary>7300-EXPORT-OF-GOODS: zero rated subject to proof of export held in time (SO-026).</summary>
    private static void ExportOfGoods(SovatArea sv, State s)
    {
        if (sv.LineType != "GOODS" && sv.LineType != "CHARGE") return;
        if (sv.DeliverLocation is "GB" or "XI") return;
        sv.VatCode = "EXP";
        sv.Liability = "ZERO";
        sv.Mechanism = "EXPORT";
        sv.TreatmentBasis = "EXPORTEVIDENCE";
        s.Decided = true;
        s.Text = Cob.Str(200, "Goods exported from Great Britain to ", Cob.Trim(sv.DeliverLocation), " - zero rated subject to proof of export");
        AddRule(sv, s);
        Evidence(sv);
        switch (sv.EvidenceStatus)
        {
            case "OBTAINED":
                s.Text = Cob.Str(200, "Proof of export ", Cob.Trim(sv.ProofType), " is held - zero rating is confirmed");
                AddRule(sv, s);
                break;
            case "PENDING":
                sv.BlockInvoice = "Y";
                sv.BlockReason = "VAT0004";
                sv.BlockText = Cob.Str(200, "Zero rating is pending proof of export, ", "due by ", Cob.D8(sv.ProofDeadline));
                sv.Header.ReasonCode = "VAT0004";
                sv.Header.Message = sv.BlockText;
                sv.Header.ReturnCode = 6;
                sv.Header.MessageCount++;
                break;
            case "OVERDUE":
                sv.BlockInvoice = "Y";
                sv.BlockReason = "VAT0005";
                sv.BlockText = Cob.Str(200, "Proof of export was due by ", Cob.D8(sv.ProofDeadline), " - UK VAT must now be accounted for");
                sv.Header.ReasonCode = "VAT0005";
                sv.Header.Message = sv.BlockText;
                sv.Header.ReturnCode = 8;
                sv.Header.MessageCount++;
                break;
        }
    }

    /// <summary>7400-DOMESTIC-REVERSE: specified goods to a VAT registered reseller above the de minimis.</summary>
    private static void DomesticReverse(SovatArea sv, State s)
    {
        if (sv.LineType != "GOODS") return;
        if (sv.ItemDrcGoods != "Y") return;
        if (sv.CustDrcElig != "Y")
        {
            s.Text = Cob.Str(200, "Customer is not a VAT registered reseller - ", "the domestic reverse charge does not apply");
            AddRule(sv, s);
            return;
        }
        if (sv.NetAmountGbp <= sv.DrcDeminimis)
        {
            s.Text = Cob.Str(200, "Below the de minimis of ", Cob.Ed(sv.DrcDeminimis, "-(11)9.99"), " so the normal UK liability applies");
            AddRule(sv, s);
            return;
        }
        sv.VatCode = "DRC";
        sv.Liability = "STANDARD";
        sv.Mechanism = "DRC";
        sv.SelfAccounts = "Y";
        sv.DrcApplies = "Y";
        sv.TreatmentBasis = "DOMESTICREVERSE";
        s.Decided = true;
        s.Text = Cob.Str(200, "Specified goods to a VAT registered reseller ", "above ", Cob.Ed(sv.DrcDeminimis, "-(11)9.99"),
            " - domestic reverse charge");
        AddRule(sv, s);
        sv.EvidenceReqd = Cob.Str(100, "A valid customer VAT registration number and ", "confirmation that the customer is not an end user");
        if (Cob.Spaces(sv.CustVatRegNo)) NoVatNumber(sv, s);
    }

    /// <summary>7500-UK-SUPPLY: the ordinary UK supply (Northern Ireland included) on the item classification.</summary>
    private static void UkSupply(SovatArea sv, State s)
    {
        sv.VatCode = Cob.X(sv.ItemVatCode, 4);
        sv.TreatmentBasis = "ITEMCLASS";
        sv.Mechanism = "NORMAL";
        sv.Liability = sv.VatCode switch
        {
            "S" => "STANDARD",
            "R" => "REDUCED",
            "Z" => "ZERO",
            "E" => "EXEMPT",
            "O" => "OUTSIDE",
            _ => "STANDARD",
        };
        s.Text = sv.DeliverLocation == "XI"
            ? Cob.Str(200, "Northern Ireland is a UK supply with UK VAT ", "- item classification ", Cob.Trim(sv.VatCode), " applies")
            : Cob.Str(200, "UK domestic supply - item classification ", Cob.Trim(sv.VatCode), " applies");
        AddRule(sv, s);
    }

    /// <summary>7900-NO-VAT-NUMBER: a reverse charge sale without a customer VAT number blocks invoicing (RC 08 VAT0003).</summary>
    private static void NoVatNumber(SovatArea sv, State s)
    {
        sv.BlockInvoice = "Y";
        sv.BlockReason = "VAT0003";
        sv.BlockText = Cob.Str(200, "A reverse charge sale needs a valid customer VAT ", "registration number before it can be invoiced");
        sv.Header.ReasonCode = "VAT0003";
        sv.Header.Message = sv.BlockText;
        sv.Header.ReturnCode = 8;
        sv.Header.MessageCount++;
        s.Text = sv.BlockText;
        AddRule(sv, s);
    }

    /// <summary>4000-RATE-AND-WORDING: baseline rate and wording, fin.VatCode/fin.VatRate when reading masters, VAT amount.</summary>
    private async Task RateAndWordingAsync(SovatArea sv, State s, CancellationToken ct)
    {
        sv.VatRatePct = sv.VatCode switch
        {
            "S" => 20.0000m,
            "R" => 5.0000m,
            _ => 0.0000m,
        };
        BaseWording(sv);
        if (sv.ReadMaster == "Y") await ReadVatCodeAsync(sv, s, ct);
        sv.VatAmount = Cob.Rd(sv.NetAmount * sv.VatRatePct / 100, 13, 2);
    }

    /// <summary>4100-READ-VAT-CODE.</summary>
    private async Task ReadVatCodeAsync(SovatArea sv, State s, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(VatCodeSql, [SqlioParm.Text(Cob.D8(sv.SupplyDate)), SqlioParm.Text(sv.VatCode)], Widths, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(sv, s, ex);
            return;
        }
        if (!row.Found) return;
        sv.Liability = Cob.X(row.Raw(0), 12);
        sv.Mechanism = Cob.X(row.Raw(1), 12);
        if (!Cob.Spaces(row.Raw(2))) sv.InvoiceWording = Cob.X(row.Raw(2), 200);
        if (!Cob.Spaces(row.Raw(3))) sv.EvidenceReqd = Cob.X(row.Raw(3), 100);
        if (Cob.Spaces(row.Raw(4)))
        {
            if (sv.VatCode is "S" or "R")
            {
                sv.Header.ReasonCode = "VAT0010";
                sv.Header.Message = Cob.Str(200, "No VAT rate is held for code ", Cob.Trim(sv.VatCode), " on the supply date");
                Reject(sv, s);
            }
        }
        else
        {
            sv.VatRatePct = Cob.St(row.NumVal(4), 5, 4);
        }
    }

    /// <summary>4200-BASE-WORDING.</summary>
    private static void BaseWording(SovatArea sv)
    {
        sv.InvoiceWording = sv.VatCode switch
        {
            "S" => "VAT at the standard rate of 20%",
            "R" => "VAT at the reduced rate of 5%",
            "Z" => "Zero-rated supply",
            "E" => "Exempt supply - no VAT is chargeable",
            "O" => Cob.Str(200, "Outside the scope of UK VAT - customer ", "to account for any VAT due"),
            "RC" => Cob.Str(200, "Reverse charge: customer to account for ", "any VAT due"),
            "DRC" => Cob.Str(200, "Reverse charge: customer to pay the VAT ", "to HMRC (VAT Act 1994 Section 55A ", "applies)"),
            "EXP" => Cob.Str(200, "Zero-rated export of goods - evidence of ", "export is retained"),
            _ => Cob.Str(200, "VAT treatment ", Cob.Trim(sv.VatCode)),
        };
    }

    /// <summary>8000-FINISH: rate, wording, amount and the closing message.</summary>
    private async Task FinishAsync(SovatArea sv, State s, CancellationToken ct)
    {
        if (sv.OverrideApplied != "Y") await RateAndWordingAsync(sv, s, ct);
        if (Cob.Spaces(sv.EvidenceReqd)) sv.EvidenceReqd = "None beyond the sales invoice";
        if (Cob.Spaces(sv.Header.Message))
        {
            sv.Header.Message = Cob.Str(200, "VAT code ", Cob.Trim(sv.VatCode), " at ", Cob.Ed(sv.VatRatePct, "-(4)9.9(4)"), "% - ",
                Cob.Trim(sv.InvoiceWording));
        }
    }

    /// <summary>9400-ADD-RULE: at most ten trace lines of 90 characters.</summary>
    private static void AddRule(SovatArea sv, State s)
    {
        if (sv.RuleCnt >= 10) return;
        sv.RuleCnt++;
        var r = sv.Rule[sv.RuleCnt - 1];
        r.RSeq = sv.RuleCnt;
        r.RText = Cob.X(s.Text, 90);
        s.Text = "";
    }

    private static void Reject(SovatArea sv, State s)
    {
        sv.Header.ReturnCode = 8;
        s.InputBad = true;
        sv.Header.MessageCount++;
    }

    /// <summary>9300-SQL-FAILED.</summary>
    private static void SqlFailed(SovatArea sv, State s, SqlioException ex)
    {
        s.InputBad = true;
        sv.Header.Message = Header.SqlMessage(ex, Program);
        sv.Header.ReasonCode = "SQL0001";
        sv.Header.ReturnCode = 12;
        sv.Header.MessageCount++;
    }
}
