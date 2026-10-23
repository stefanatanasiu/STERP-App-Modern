using Sterp.Modules.Kit.Common;
using Sterp.Platform.Kernel;

namespace Sterp.Modules.Kit.Application;

/// <summary>KitService.Trial / MaxBuild (KitService.vb:100-116,129-148) over KITTRIAL.</summary>
internal sealed class KitTrialService(KitTrial kitTrial)
{
    /// <summary>KitService.TrialArea: a new KITTRIALCA; selection date = business date, committed N, from-line 0, window 20.</summary>
    private static KitTrialArea TrialArea(KitSession s, string kitRef, string parentItem, string revision, string site, decimal qty,
        int requiredBy, bool useInbound, bool useSubassembly) => new()
    {
        KitRef = Pic.X(kitRef, 30),
        ParentItem = Pic.X(parentItem, 40),
        ParentRev = Pic.X(revision, 4),
        KtSite = Pic.X(site, 10),
        RequestedQty = qty,
        ReqDate = requiredBy,
        SelectionDate = s.BusinessDate,
        UseInbound = useInbound ? "Y" : "N",
        UseSubassy = useSubassembly ? "Y" : "N",
        Committed = "N",
        FromLine = 0,
        WinMax = 20,
    };

    private async Task<LegacyResult> LinkAsync(KitTrialArea ca, KitSession s, string function, CancellationToken ct)
    {
        ca.Prepare(s, function);
        await kitTrial.LinkAsync(ca, ct);
        return ca.Result;
    }

    /// <summary>KitService.Trial: TRIAL, then GETLINE while more lines (KitArea.Page, 0-based offsets).</summary>
    public async Task<(LegacyResult Result, KitTrialArea Area, List<KitTrialLine> Lines)> TrialAsync(KitSession s, string kitRef, string parentItem,
        string revision, string site, decimal qty, int requiredBy, bool useInbound, bool useSubassembly, CancellationToken ct)
    {
        var ca = TrialArea(s, kitRef, parentItem, revision, site, qty, requiredBy, useInbound, useSubassembly);
        var lines = new List<KitTrialLine>();
        var r = await LinkAsync(ca, s, "TRIAL", ct);
        if (!r.Succeeded) return (r, ca, lines);
        var from = 0;
        for (var guard = 1; guard <= 100; guard++)
        {
            var n = ca.WinCnt;
            for (var i = 0; i < n; i++) lines.Add(ca.Lines[i].Copy());
            from += n;
            if (n == 0 || ca.MoreLines != "Y") break;
            ca.FromLine = from;
            r = await LinkAsync(ca, s, "GETLINE", ct);
            if (!r.Succeeded) break;
        }
        return (r, ca, lines);
    }

    /// <summary>KitService.MaxBuild: MAXBUILD with subassemblies off; the summary only (the facade does not page lines).</summary>
    public async Task<(LegacyResult Result, KitTrialArea Area)> MaxBuildAsync(KitSession s, string kitRef, string parentItem, string revision,
        string site, decimal qty, int requiredBy, bool useInbound, CancellationToken ct)
    {
        var ca = TrialArea(s, kitRef, parentItem, revision, site, qty, requiredBy, useInbound, false);
        var r = await LinkAsync(ca, s, "MAXBUILD", ct);
        return (r, ca);
    }
}
