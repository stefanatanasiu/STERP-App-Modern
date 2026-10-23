using System.Text.Json.Nodes;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Fin.Vat;

/// <summary>VATERRCCA body fields of ASSESS (cobol/copy/VATERRCCA.cpy).</summary>
public sealed class VatErrArea
{
    public string OrigPeriodKey { get; set; } = "";
    public string CorrPeriodKey { get; set; } = "";
    public int DiscoveredDate { get; set; }
    public decimal NetError { get; set; }
    public decimal Box6Turnover { get; set; }
    public string IsDeliberate { get; set; } = "";

    public string Method { get; set; } = "";
    public decimal ThresholdUsed { get; set; }
    public decimal OnePctLimit { get; set; }
    public decimal FlatLimit { get; set; }
    public decimal CapLimit { get; set; }
    public string WithinThreshold { get; set; } = "N";
    public string Status { get; set; } = "";
    public string Explanation { get; set; } = "";

    public JsonObject ToJson() => new()
    {
        ["method"] = Method,
        ["thresholdUsed"] = CobolNumeric.WithScale(ThresholdUsed, 2),
        ["onePctLimit"] = CobolNumeric.WithScale(OnePctLimit, 2),
        ["flatLimit"] = CobolNumeric.WithScale(FlatLimit, 2),
        ["capLimit"] = CobolNumeric.WithScale(CapLimit, 2),
        ["withinThreshold"] = WithinThreshold,
        ["status"] = Status,
        ["explanation"] = Explanation,
    };
}

/// <summary>VATERRC (cobol/online/fin/VATERRC.cob) function ASSESS: next return or separate disclosure. Writes nothing.</summary>
public sealed class VatErrorProgram(IFinKernel kernel)
{
    public async Task<CaHeader> RunAsync(CaRequest req, VatErrArea ve, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-027");
        ve.Method = ve.Explanation = "";
        ve.WithinThreshold = "N";
        ve.ThresholdUsed = ve.OnePctLimit = ve.FlatLimit = ve.CapLimit = 0;
        if (ve.DiscoveredDate == 0) ve.DiscoveredDate = req.BusinessDate;
        if (ve.IsDeliberate != "Y") ve.IsDeliberate = "N";
        if (req.Function != "ASSESS")
        {
            ca.Reject("VEC0009", "VATERRC does not support function " + req.Function.Trim());
            return ca;
        }
        await AssessAsync(req, ca, ve, ct);
        return ca;
    }

    // 1000-ASSESS
    private async Task AssessAsync(CaRequest req, CaHeader ca, VatErrArea ve, CancellationToken ct)
    {
        await ReadLimitsAsync(req, ve, ct);
        var absError = CobolNumeric.Store(Math.Abs(ve.NetError), 13, 2);
        ve.OnePctLimit = CobolNumeric.Store(ve.Box6Turnover / 100, 13, 2, rounded: true);
        if (ve.OnePctLimit > ve.CapLimit) ve.OnePctLimit = ve.CapLimit;
        ve.ThresholdUsed = ve.OnePctLimit > ve.FlatLimit ? ve.OnePctLimit : ve.FlatLimit;
        if (ve.IsDeliberate == "Y")
        {
            ve.Method = "DISCLOSURE";
            ve.WithinThreshold = "N";
            ve.Explanation = "A deliberate error is always disclosed separately";
            ca.ReturnCode = 6;
            ca.ReasonCode = "VEC0010";
            ca.MsgCount++;
        }
        else if (absError <= ve.ThresholdUsed)
        {
            ve.Method = "NEXTRETURN";
            ve.WithinThreshold = "Y";
            ve.Explanation = "Within the approved threshold, so it may be corre";
        }
        else
        {
            ve.Method = "DISCLOSURE";
            ve.WithinThreshold = "N";
            ve.Explanation = "Above the approved threshold, so it is routed for";
            ca.ReturnCode = 6;
            ca.ReasonCode = "VEC0011";
            ca.MsgCount++;
        }
        // 1050-MESSAGE
        ca.Message = ve.WithinThreshold == "Y"
            ? "The net error may be corrected on the next return. The threshold applied is " + FinEdit.Money(ve.ThresholdUsed) + " GBP"
            : "The net error exceeds the threshold of " + FinEdit.Money(ve.ThresholdUsed) + " GBP and must be disclosed separately to HMRC";
    }

    // 1100-READ-LIMITS
    private async Task ReadLimitsAsync(CaRequest req, VatErrArea ve, CancellationToken ct)
    {
        ve.FlatLimit = 10000;
        var pm = await kernel.ParmGetAsync(req.Callee("GETNUM", site: false, correlation: false), "VAT.ERROR.THRESHOLD", ve.DiscoveredDate, numeric: true, ct);
        if (pm.Found) ve.FlatLimit = CobolNumeric.Store(pm.ValueNum, 11, 2);
        ve.CapLimit = 50000;
        pm = await kernel.ParmGetAsync(req.Callee("GETNUM", site: false, correlation: false), "VAT.ERROR.CAP", ve.DiscoveredDate, numeric: true, ct);
        if (pm.Found) ve.CapLimit = CobolNumeric.Store(pm.ValueNum, 11, 2);
    }
}
