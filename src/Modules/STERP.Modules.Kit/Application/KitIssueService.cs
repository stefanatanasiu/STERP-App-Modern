using Sterp.Modules.Kit.Common;
using Sterp.Platform.Kernel;

namespace Sterp.Modules.Kit.Application;

/// <summary>
/// KitExecutionService (dotnet/STERP.Business/Kits/KitExecutionService.vb) for KITISSUE: the facade parameter rules -
/// site = the session site, dates = business date, approval ids never sent.
/// </summary>
internal sealed class KitIssueService(KitIssue kitIssue)
{
    /// <summary>KitExecutionService.Issue (KitExecutionService.vb:53-77).</summary>
    public async Task<(LegacyResult Result, KitIssueArea Area)> IssueAsync(KitSession s, string kitRef, int lineNum, string item, string lot,
        string serial, decimal qty, string location, string reasonCode, string reasonText, bool allowOver, bool checkOnly, CancellationToken ct)
    {
        var ca = new KitIssueArea
        {
            KitRef = Pic.X(kitRef, 30),
            LineNum = lineNum,
            ItemCode = Pic.X(item, 40),
            KsSite = Pic.X(s.Site, 10),
            Location = Pic.X(location, 20),
            LotCode = Pic.X(lot, 30),
            SerialNo = Pic.X(serial, 40),
            Quantity = qty,
            ActualDate = s.BusinessDate,
            PostingDate = s.BusinessDate,
            ReasonCodeIn = Pic.X(reasonCode, 20),
            ReasonText = Pic.X(reasonText, 300),
            AllowOver = allowOver ? "Y" : "N",
            CheckOnly = checkOnly ? "Y" : "N",
        };
        ca.Prepare(s, checkOnly ? "CHECK" : "ISSUE");
        await kitIssue.LinkAsync(ca, ct);
        return (ca.Result, ca);
    }

    /// <summary>KitExecutionService.Backflush (KitExecutionService.vb:83-93).</summary>
    public async Task<(LegacyResult Result, KitIssueArea Area)> BackflushAsync(KitSession s, string kitRef, decimal goodQty, CancellationToken ct)
    {
        var ca = new KitIssueArea
        {
            KitRef = Pic.X(kitRef, 30),
            KsSite = Pic.X(s.Site, 10),
            GoodQty = goodQty,
            ActualDate = s.BusinessDate,
            PostingDate = s.BusinessDate,
        };
        ca.Prepare(s, "BACKFLSH");
        await kitIssue.LinkAsync(ca, ct);
        return (ca.Result, ca);
    }
}
