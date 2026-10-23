using Sterp.Modules.Kit.Common;

namespace Sterp.Modules.Kit.Domain;

/// <summary>
/// KITQTY LINEQTY (cobol/online/kit/KITQTY.cob:99-149; FR-STERP-KIT-061): the derived quantities of a kit material line.
/// Stateless (LOCAL-STORAGE). The callers INITIALIZE the area first and ignore the return code, so a refused derivation
/// leaves net issued, remaining and excess at zero (spec 4.1 rule 4).
/// </summary>
internal static class KitQty
{
    private const decimal Tolerance = 0.000001m;

    /// <summary>The KITQTYCA outputs of one LINEQTY call.</summary>
    internal sealed record Result(int ReturnCode, string ReasonCode, string Message, decimal NetIssued, decimal Remaining, decimal Excess, string ImpactText);

    public static Result LineQty(decimal grossIssued, decimal returned, decimal scrapped, decimal currentReq)
    {
        // Validate-Movement-Totals
        if (grossIssued < 0 || returned < 0 || scrapped < 0 || currentReq < 0)
        {
            return new Result(8, "KIT0003", "Issued, returned, scrapped and requirement quantities must not be negative.", 0m, 0m, 0m, "");
        }
        if (returned > grossIssued + Tolerance)
        {
            return new Result(8, "KIT0011", "Returned quantity exceeds the quantity issued to this kit line.", 0m, 0m, 0m, "");
        }
        var net = Pic.Q(grossIssued - returned);
        if (net < 0) net = 0m;
        var remaining = Pic.Q(currentReq - net);
        if (remaining < 0) remaining = 0m;
        var excess = Pic.Q(net - currentReq);
        if (excess < 0) excess = 0m;
        if (excess > Tolerance)
        {
            var ed = Pic.Ed(excess);
            return new Result(4, "KIT0022", Pic.Msg("Excess material of ", ed, " is on this line and needs a disposition."),
                net, remaining, excess, Pic.Str(200, "EXCESS ", ed, " - return or obtain an approved disposition"));
        }
        return new Result(0, "", "", net, remaining, excess, "");
    }
}
