namespace Sterp.Modules.Po.Programs;

/// <summary>TOLCHKCA body (cobol/copy/TOLCHKCA.cpy, prefix TC-) - the fields the QTY test uses.</summary>
internal sealed class TolchkArea
{
    public string OrderNumber { get; set; } = "";
    public int LineNum { get; set; }
    public string Supplier { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public string RuleSource { get; set; } = "";
    public decimal OrderedQty { get; set; }
    public decimal CancelledQty { get; set; }
    public decimal CountedQty { get; set; }
    public decimal ProposedQty { get; set; }
    public decimal OverPct { get; set; }
    public decimal OverQtyAllow { get; set; }
    public string ActionOver { get; set; } = "";
    public decimal PricePctLim { get; set; }
    public decimal PriceAbsLim { get; set; }
    public decimal ChargeAbsLim { get; set; }
    public int EarlyDayLim { get; set; }
    public decimal PermittedQty { get; set; }
    public decimal OutstandingQty { get; set; }
    public decimal OverQty { get; set; }
    public decimal ExcessQty { get; set; }
    public string Outcome { get; set; } = "";
    public string NeedsAmend { get; set; } = "";
    public string RuleScope { get; set; } = "";
    public string RuleScopeCode { get; set; } = "";
}

/// <summary>
/// TOLCHK (cobol/online/po/TOLCHK.cob) QTY with caller-passed limits (RULE-SOURCE PASSED, the only use by RECEIPT): the
/// D-08 over-delivery arithmetic. The PRICE, DATE and CHARGE tests and the LOOKUP rule source have no delivered caller
/// (spec 12.2, SME-1589) and are not delivered. No table access.
/// </summary>
internal sealed class TolchkProgram
{
    private const decimal PolOverMaxPct = 5m;
    private const decimal PolPricePct = 2m;
    private const decimal PolPriceAbs = 50m;
    private const decimal PolChargeAbs = 25m;
    private const int PolEarlyDays = 5;

    public void Run(CaHeader ca, TolchkArea tc)
    {
        ca.Program = "TOLCHK";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        tc.PermittedQty = 0; tc.OutstandingQty = 0;
        tc.OverQty = 0; tc.ExcessQty = 0;
        tc.Outcome = "WITHIN";
        tc.NeedsAmend = "N";
        if (Cob.Spaces(tc.RuleSource)) tc.RuleSource = "PASSED";
        if (tc.RuleSource == "PASSED")
        {
            tc.RuleScope = "CALLER";
            tc.RuleScopeCode = "";
            if (tc.PricePctLim == 0) tc.PricePctLim = PolPricePct;
            if (tc.PriceAbsLim == 0) tc.PriceAbsLim = PolPriceAbs;
            if (tc.ChargeAbsLim == 0) tc.ChargeAbsLim = PolChargeAbs;
            if (tc.EarlyDayLim == 0) tc.EarlyDayLim = PolEarlyDays;
            if (Cob.Spaces(tc.ActionOver)) tc.ActionOver = "BLOCK";
        }
        if (tc.OverPct > PolOverMaxPct)
        {
            tc.OverPct = PolOverMaxPct;
            ca.ReasonCode = "TOL0009";
            ca.Message = Cob.Msg("The over delivery override has been capped at the policy maximum of 5%");
            ca.ReturnCode = 4;
        }
        if (tc.RuleSource == "LOOKUP")
        {
            throw new NotSupportedException("TOLCHK LOOKUP rules have no delivered caller (STERP-PO 12.2).");
        }
        switch (ca.Function)
        {
            case "QTY":
                Qty(ca, tc);
                break;
            case "PRICE" or "DATE" or "CHARGE":
                throw new NotSupportedException($"TOLCHK {ca.Function} has no delivered caller (STERP-PO 12.2, SME-1589).");
            default:
                ca.ReasonCode = "TOL0008";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by TOLCHK");
                ca.ReturnCode = 8;
                break;
        }
    }

    /// <summary>2000-QTY.</summary>
    private static void Qty(CaHeader ca, TolchkArea tc)
    {
        if (tc.OrderedQty <= 0)
        {
            ca.ReasonCode = "TOL0001";
            ca.Message = Cob.Msg("The ordered quantity must be greater than zero");
            ca.ReturnCode = 8;
            tc.Outcome = "BLOCK";
            return;
        }
        var approved = Cob.T(tc.OrderedQty - tc.CancelledQty, 13, 6);
        if (approved < 0) approved = 0;
        var allowPct = Cob.R(approved * tc.OverPct / 100m, 13, 6);
        var allow = allowPct;
        if (tc.OverQtyAllow > allow) allow = tc.OverQtyAllow;
        tc.PermittedQty = Cob.T(approved + allow, 13, 6);
        var after = Cob.T(tc.CountedQty + tc.ProposedQty, 13, 6);
        tc.OutstandingQty = Cob.T(approved - after, 13, 6);
        if (tc.OutstandingQty < 0) tc.OutstandingQty = 0;
        tc.OverQty = Cob.T(after - approved, 13, 6);
        if (tc.OverQty < 0) tc.OverQty = 0;
        tc.ExcessQty = Cob.T(after - tc.PermittedQty, 13, 6);
        if (tc.ExcessQty < 0) tc.ExcessQty = 0;
        if (tc.ExcessQty > 0)
        {
            tc.Outcome = "BLOCK";
            tc.NeedsAmend = "Y";
            ca.ReasonCode = "TOL0002";
            ca.Message = Cob.Msg("The receipt is ", Cob.Qty(tc.ExcessQty),
                " above the approved over delivery limit; an approved order amendment is required before it can be confirmed");
            if (tc.ActionOver == "WARN")
            {
                ca.ReturnCode = 4;
                tc.Outcome = "WARN";
            }
            else
            {
                ca.ReturnCode = 6;
            }
        }
        else if (after == tc.PermittedQty && tc.OverQty > 0)
        {
            tc.Outcome = "ATLIMIT";
            ca.ReasonCode = "TOL0003";
            ca.Message = Cob.Msg("Receipt accepted at the approved over delivery limit; ", Cob.Qty(tc.OverQty),
                " excess units are accrued and separately visible");
            ca.ReturnCode = 4;
        }
        else if (tc.OverQty > 0)
        {
            tc.Outcome = "WARN";
            ca.ReasonCode = "TOL0003";
            ca.Message = Cob.Msg("Receipt accepted within the approved over delivery tolerance; ", Cob.Qty(tc.OverQty),
                " excess units are accrued");
            ca.ReturnCode = 4;
        }
        else
        {
            tc.Outcome = "WITHIN";
            ca.Message = Cob.Msg("Receipt within the approved quantity; ", Cob.Qty(tc.OutstandingQty), " remain outstanding");
            ca.ReturnCode = 0;
        }
    }
}
