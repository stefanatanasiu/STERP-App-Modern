using Sterp.Modules.Bom.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Data;

/// <summary>One BS-CANDIDATES entry.</summary>
internal sealed record BillCandidate(int BomId, string BomRef, string BomRev, string Status, string Site, string Variant, string Purpose,
    int EffFrom, int EffTo, string Eligible, string Reason);

/// <summary>BOMSELCTCA (cobol/copy/BOMSELCTCA.cpy): request and result of one BOMSELCT call.</summary>
internal sealed class SelectArea
{
    public string Function { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public int BusinessDate { get; set; }
    public int ReturnCode { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>CA-MESSAGE X(200).</summary>
    public string Message { get; set; } = "";
    public int MessageCount { get; set; }

    public string ParentItem { get; set; } = "";
    public string ParentRev { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string JobRef { get; set; } = "";
    public string Variant { get; set; } = "";
    public string WantRev { get; set; } = "";
    public int EffDate { get; set; }
    public string AllowDraft { get; set; } = "";

    public int BomId { get; set; }
    public string BomRef { get; set; } = "";
    public string BomRev { get; set; } = "";
    public string BomStatus { get; set; } = "";
    public string BomSite { get; set; } = "";
    public int EffFrom { get; set; }
    public int EffTo { get; set; }
    public decimal NominalBasis { get; set; }
    public decimal Yield { get; set; }
    public decimal Quantum { get; set; }
    public decimal BatchSize { get; set; }
    public string IsPhantom { get; set; } = "";
    public string Consolidate { get; set; } = "";
    public int ComponentCount { get; set; }
    public List<BillCandidate> Candidates { get; } = [];
}

/// <summary>
/// BOMSELCT (cobol/online/bom/BOMSELCT.cob): deterministic bill selection - SELECT (exactly one applicable released bill),
/// PIN (a deliberately chosen revision) or LIST, with half-open effectivity and the reason each candidate was rejected.
/// </summary>
internal sealed class BillSelector(ISqlRunner sql)
{
    private const string CandidateSql =
        "SELECT h.BomHeaderId, h.BomRef, h.BomRevision, h.Status, ISNULL(h.SiteCode,''), ISNULL(h.VariantCode,''), h.Purpose, " +
        "ISNULL(h.ParentRevision,''), ISNULL(h.JobRef,''), CONVERT(char(8),h.EffectiveFrom,112), ISNULL(CONVERT(char(8),h.EffectiveTo,112),'00000000'), " +
        "h.NominalBasis, h.YieldFactor, h.ProductionQuantum, ISNULL(h.BatchSize,0), CASE WHEN h.IsPhantom=1 THEN 'Y' ELSE 'N' END, " +
        "CASE WHEN h.ConsolidateRuns=1 THEN 'Y' ELSE 'N' END, (SELECT COUNT(*) FROM bom.BomComponent c WHERE c.BomHeaderId = h.BomHeaderId) " +
        "FROM bom.BomHeader h WHERE h.CompanyCode = ? AND h.ParentItemCode = ? AND h.Purpose = ? ORDER BY h.BomRevision, h.BomHeaderId";

    private const int CandMax = 20;

    public async Task CallAsync(SelectArea a, CancellationToken ct)
    {
        // Initialise-Call
        var rejected = false;
        a.ReturnCode = 0;
        a.MessageCount = 0;
        a.Reason = "";
        a.Message = "";
        a.BomId = 0;
        a.Candidates.Clear();
        a.EffFrom = 0;
        a.EffTo = 0;
        a.ComponentCount = 0;
        a.NominalBasis = 0m;
        a.Yield = 0m;
        a.Quantum = 0m;
        a.BatchSize = 0m;
        a.BomRef = "";
        a.BomRev = "";
        a.BomStatus = "";
        a.BomSite = "";
        a.IsPhantom = "N";
        a.Consolidate = "N";
        a.ParentItem = Pic.X(Pic.Upper(a.ParentItem), 40);
        if (Pic.Blank(a.Purpose)) a.Purpose = "STANDARD";
        if (a.EffDate == 0) a.EffDate = a.BusinessDate;
        if (Pic.Blank(a.ParentItem))
        {
            a.Reason = "BOM0201";
            a.Message = "A parent item is required to select a bill";
            RejectInput();
        }
        else if (!ValidDate(a.EffDate))
        {
            a.Reason = "BOM0202";
            a.Message = "The effective date is not a valid calendar date";
            RejectInput();
        }
        else if (a.Purpose != "STANDARD" && Pic.Blank(a.JobRef))
        {
            a.Reason = "BOM0208";
            a.Message = Pic.Str(200, "A ", Pic.T(a.Purpose), " bill must be named explicitly by its job or order reference");
            RejectInput();
        }
        if (rejected) return;

        switch (a.Function)
        {
            case "SELECT":
            case "PIN":
            case "LIST":
                var (failed, eligibleCnt, chosen, wantHit) = await LoadCandidatesAsync(a, ct);
                if (failed) return;
                if (a.Function == "SELECT") ChooseOne(a, eligibleCnt, chosen);
                else if (a.Function == "PIN") ChoosePinned(a, wantHit);
                break;
            default:
                a.Reason = "BOM0210";
                a.Message = Pic.Str(200, "BOMSELCT does not support the function ", Pic.T(a.Function));
                a.ReturnCode = 8;
                break;
        }

        void RejectInput()
        {
            a.ReturnCode = 8;
            a.MessageCount += 1;
            rejected = true;
        }
    }

    private static bool ValidDate(int yyyymmdd)
    {
        var y = yyyymmdd / 10000;
        var m = yyyymmdd / 100 % 100;
        var d = yyyymmdd % 100;
        return y is >= 1601 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m);
    }

    private async Task<(bool Failed, int Eligible, int Chosen, int WantHit)> LoadCandidatesAsync(SelectArea a, CancellationToken ct)
    {
        var eligible = 0;
        var chosen = 0;
        var wantHit = 0;
        try
        {
            await sql.WithReaderAsync(BomSql.Q(CandidateSql), BomSql.Args(a.Company, a.ParentItem, a.Purpose), async r =>
            {
                while (await r.ReadAsync(ct))
                {
                    // Examine-Candidate
                    if (a.Candidates.Count >= CandMax)
                    {
                        a.ReturnCode = 4;
                        a.Reason = "BOM0211";
                        a.Message = "More candidate bills exist than can be listed";
                        continue;
                    }
                    var bomId = (int)BomSql.Num(r, 0);
                    var bomRef = BomSql.Text(r, 1, 30);
                    var bomRev = BomSql.Text(r, 2, 4);
                    var status = BomSql.Text(r, 3, 12);
                    var site = BomSql.Text(r, 4, 10);
                    var variant = BomSql.Text(r, 5, 20);
                    var purpose = BomSql.Text(r, 6, 10);
                    var parentRev = BomSql.Text(r, 7, 4);
                    var jobRef = BomSql.Text(r, 8, 20);
                    var from = BomSql.Date8(r, 9);
                    var to = BomSql.Date8(r, 10);
                    var ok = "Y";
                    string reason;
                    if (status == "RETIRED") { ok = "N"; reason = "Retired"; }
                    else if (status == "SUPERSEDED") { ok = "N"; reason = "Superseded by a later revision"; }
                    else if (status != "RELEASED" && a.AllowDraft != "Y") { ok = "N"; reason = "Not released - review not complete"; }
                    else if (from > a.EffDate) { ok = "N"; reason = "Not yet effective on the stated date"; }
                    else if (to != 0 && to <= a.EffDate) { ok = "N"; reason = "Effectivity ended before the stated date"; }
                    else if (!Pic.Blank(site) && !Pic.Blank(a.Site) && site != a.Site) { ok = "N"; reason = "Restricted to another site"; }
                    else if (!Pic.Blank(variant) && variant != a.Variant) { ok = "N"; reason = "Belongs to another product variant"; }
                    else if (!Pic.Blank(parentRev) && !Pic.Blank(a.ParentRev) && parentRev != a.ParentRev) { ok = "N"; reason = "Written for another parent item revision"; }
                    else if (!Pic.Blank(a.JobRef) && jobRef != a.JobRef) { ok = "N"; reason = "Belongs to another job or order"; }
                    else reason = "Applicable";
                    a.Candidates.Add(new BillCandidate(bomId, bomRef, bomRev, status, site, variant, purpose, from, to, ok, reason));
                    var index = a.Candidates.Count;
                    if (ok == "Y")
                    {
                        eligible++;
                        chosen = index;
                    }
                    if (!Pic.Blank(a.WantRev) && bomRev == a.WantRev)
                    {
                        wantHit = index;
                        KeepSelected(a, r);
                    }
                    if (Pic.Blank(a.WantRev) && ok == "Y") KeepSelected(a, r);
                }
                return true;
            }, ct);
        }
        catch (SqlioException ex)
        {
            a.ReturnCode = 12;
            a.Reason = "SQL0001";
            a.Message = BomSql.ErrMsg(ex, 200);
            return (true, eligible, chosen, wantHit);
        }
        return (false, eligible, chosen, wantHit);
    }

    private static void KeepSelected(SelectArea a, System.Data.Common.DbDataReader r)
    {
        a.NominalBasis = Pic.N(BomSql.Num(r, 11), 13, 6);
        a.Yield = Pic.N(BomSql.Num(r, 12), 5, 6);
        a.Quantum = Pic.N(BomSql.Num(r, 13), 13, 6);
        a.BatchSize = Pic.N(BomSql.Num(r, 14), 13, 6);
        a.IsPhantom = BomSql.Text(r, 15, 1);
        a.Consolidate = BomSql.Text(r, 16, 1);
        a.ComponentCount = Pic.I((long)BomSql.NumVal(BomSql.Text(r, 17, 8)), 4);
    }

    private static void ChooseOne(SelectArea a, int eligible, int chosen)
    {
        if (eligible == 1)
        {
            PublishChosen(a, chosen);
        }
        else if (eligible == 0)
        {
            a.ReturnCode = 16;
            a.Reason = "BOM0203";
            a.Message = Pic.Str(200, "No applicable released bill for ", Pic.T(a.ParentItem), " on the stated effective date");
        }
        else
        {
            a.ReturnCode = 8;
            a.Reason = "BOM0204";
            a.Message = Pic.Str(200, "Overlapping released bills for ", Pic.T(a.ParentItem), " - the context must identify exactly one");
            a.BomId = 0;
        }
    }

    private static void ChoosePinned(SelectArea a, int wantHit)
    {
        if (wantHit == 0)
        {
            a.ReturnCode = 16;
            a.Reason = "BOM0205";
            a.Message = Pic.Str(200, "Revision ", Pic.T(a.WantRev), " does not exist for ", Pic.T(a.ParentItem));
            return;
        }
        var c = a.Candidates[wantHit - 1];
        if (c.Eligible == "Y")
        {
            PublishChosen(a, wantHit);
            return;
        }
        a.ReturnCode = 8;
        a.Reason = "BOM0206";
        a.Message = Pic.Str(200, "Revision ", Pic.T(a.WantRev), " cannot be used: ", Pic.T(c.Reason));
        a.BomId = c.BomId;
        a.BomRev = c.BomRev;
        a.BomStatus = c.Status;
    }

    private static void PublishChosen(SelectArea a, int chosen)
    {
        var c = a.Candidates[chosen - 1];
        a.BomId = c.BomId;
        a.BomRef = c.BomRef;
        a.BomRev = c.BomRev;
        a.BomStatus = c.Status;
        a.BomSite = c.Site;
        a.EffFrom = c.EffFrom;
        a.EffTo = c.EffTo;
        a.ReturnCode = 0;
        a.Message = Pic.Str(200, "Bill ", Pic.T(a.BomRef), " revision ", Pic.T(a.BomRev), " selected");
        if (a.BomStatus != "RELEASED")
        {
            a.ReturnCode = 4;
            a.Reason = "BOM0209";
            a.Message = Pic.Str(200, "Bill ", Pic.T(a.BomRef), " revision ", Pic.T(a.BomRev), " is a draft and cannot produce an approved result");
        }
    }
}
