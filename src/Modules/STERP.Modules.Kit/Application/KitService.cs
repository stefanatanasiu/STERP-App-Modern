using Sterp.Modules.Kit.Common;
using Sterp.Platform.Kernel;

namespace Sterp.Modules.Kit.Application;

/// <summary>
/// KitService (dotnet/STERP.Business/Kits/KitService.vb) for KITCREAT: fills the COMMAREA as the facade did, calls the
/// engine through the gateway header (CobolGateway.PrepareHeader) and drains the line window (KitArea.Page,
/// KitArea.vb:66-81) inside the request.
/// </summary>
internal sealed class KitService(KitCreat kitCreat)
{
    /// <summary>KitService.Header (KitService.vb:119-127): a new (INITIALIZEd) KITCREATCA with reference, kind and site.</summary>
    private static KitCreatArea Header(string kitRef, string kind, string site) => new()
    {
        KitRef = Pic.X(kitRef, 30),
        KitKind = Pic.X(kind, 12),
        KcSite = Pic.X(site, 10),
    };

    private async Task<LegacyResult> LinkAsync(KitCreatArea ca, KitSession s, string function, CancellationToken ct)
    {
        ca.Prepare(s, function);
        await kitCreat.LinkAsync(ca, ct);
        return ca.Result;
    }

    /// <summary>KitService.Enquire (KitService.vb:76-83): ENQUIRE, then GETLINE while more lines (guard 100).</summary>
    public async Task<(LegacyResult Result, KitCreatArea Area, List<KitCreatLine> Lines)> EnquireAsync(KitSession s, string kitRef, CancellationToken ct)
    {
        var ca = Header(kitRef, "", s.Site);
        ca.FromLine = 0;
        ca.WinMax = 20;
        var lines = new List<KitCreatLine>();
        var r = await LinkAsync(ca, s, "ENQUIRE", ct);
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

    /// <summary>KitService.Review (KitService.vb:64-66): no explicit correlation id.</summary>
    public async Task<(LegacyResult Result, KitCreatArea Area)> ReviewAsync(KitSession s, string kitRef, CancellationToken ct)
    {
        var ca = Header(kitRef, "", s.Site);
        var r = await LinkAsync(ca, s, "REVIEW", ct);
        return (r, ca);
    }

    /// <summary>KitService.Release (KitService.vb:69-73): KC-OWNER-USER = user, one correlation id per instruction.</summary>
    public async Task<(LegacyResult Result, KitCreatArea Area)> ReleaseAsync(KitSession s, string kitRef, CancellationToken ct)
    {
        var ca = Header(kitRef, "", s.Site);
        ca.OwnerUser = Pic.X(s.User, 20);
        var r = await LinkAsync(ca, s, "RELEASE", ct);
        return (r, ca);
    }

    /// <summary>KitService.KindCheck (KitService.vb:89-93).</summary>
    public async Task<(LegacyResult Result, KitCreatArea Area)> KindCheckAsync(KitSession s, string kitRef, string kind, string parentItem, CancellationToken ct)
    {
        var ca = Header(kitRef, kind, s.Site);
        ca.ParentItem = Pic.X(parentItem, 40);
        var r = await LinkAsync(ca, s, "KINDCHK", ct);
        return (r, ca);
    }
}
