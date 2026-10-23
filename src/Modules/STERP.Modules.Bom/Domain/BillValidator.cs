using Sterp.Modules.Bom.Data;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Domain;

/// <summary>One BV-FINDINGS entry.</summary>
internal sealed class ValidationFinding
{
    /// <summary>BV-F-SEVERITY X(8): ERROR or WARNING.</summary>
    public string Severity { get; init; } = "";
    public string Reason { get; init; } = "";
    public int LineNum { get; init; }
    public string Item { get; init; } = "";
    /// <summary>BV-F-PATH X(80).</summary>
    public string Path { get; init; } = "";
    /// <summary>BV-F-MESSAGE X(120).</summary>
    public string Message { get; init; } = "";
}

/// <summary>BOMVALIDCA (cobol/copy/BOMVALIDCA.cpy).</summary>
internal sealed class ValidationArea
{
    public string Function { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public int BusinessDate { get; set; }
    public int ReturnCode { get; set; }
    public string Reason { get; set; } = "";
    public string Message { get; set; } = "";
    public int MessageCount { get; set; }

    public int BomId { get; set; }
    public string BomRef { get; set; } = "";
    public string BomRev { get; set; } = "";
    public string ParentItem { get; set; } = "";
    public int EffDate { get; set; }
    public string ValidRef { get; set; } = "";
    public int ValidId { get; set; }
    public string Outcome { get; set; } = "";
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int ComponentCount { get; set; }
    public string CycleFound { get; set; } = "";
    /// <summary>BV-CYCLE-PATH X(200).</summary>
    public string CyclePath { get; set; } = "";
    public string ReleaseOk { get; set; } = "";
    public int FindMax { get; set; }
    public List<ValidationFinding> Findings { get; } = [];
    public int FindCount => Findings.Count;
}

/// <summary>
/// BOMVALID (cobol/online/bom/BOMVALID.cob) <c>CHECK</c>: complete validation of one bill read by its identity (spec 4.8,
/// FR-STERP-BOM-016): header, component, alternate-group, by-product and cycle rules; FAIL RC 8 BOM0340, WARNING RC 4
/// BOM0341, PASS RC 0; at most 50 findings while the counts include all. Writes nothing. The recording function
/// <c>VALIDATE</c> has no production route and is deferred (dispositions cobol:BOMVALID#VALIDATE).
/// </summary>
internal sealed class BillValidator(ISqlRunner sql, BillReader reader, BillSelector selector, CycleChecker cycles)
{
    private const string ParentClassSql =
        "SELECT CASE WHEN i.IsManufacturable = 1 AND i.ItemClass IN ('MADE','PHANTOM','KIT') THEN 'Y' ELSE 'N' END " +
        "FROM inv.Item i WHERE i.CompanyCode = ? AND i.ItemCode = ?";

    private const string ByProductSql =
        "SELECT COUNT(*) FROM bom.BomByProduct p WHERE p.BomHeaderId = ? AND (p.RecoveryQty <= 0 OR p.ApprovedBy IS NULL)";

    private sealed class Run
    {
        public ValidationArea A = null!;
        public BillRead Bill = null!;
        public int Errors;
        public int Warnings;
        public CancellationToken Ct;
    }

    public async Task CallAsync(ValidationArea a, CancellationToken ct)
    {
        a.ReturnCode = 0;
        a.MessageCount = 0;
        a.Reason = "";
        a.Message = "";
        a.FindMax = 50;
        a.Findings.Clear();
        a.ErrorCount = 0;
        a.WarningCount = 0;
        a.ComponentCount = 0;
        a.ValidId = 0;
        a.CycleFound = "N";
        a.ReleaseOk = "N";
        a.CyclePath = "";
        a.Outcome = "";
        if (a.EffDate == 0) a.EffDate = a.BusinessDate;
        if (a.Function != "CHECK")
        {
            a.ReturnCode = 8;
            a.Reason = "BOM0301";
            a.Message = Pic.Str(200, "BOMVALID does not support the function ", Pic.T(a.Function));
            return;
        }
        var run = new Run { A = a, Ct = ct };
        await ValidateTheBillAsync(run);
    }

    private async Task ValidateTheBillAsync(Run run)
    {
        if (!await ReadTheBillAsync(run)) return;
        await CheckTheHeaderAsync(run);
        await CheckEveryComponentAsync(run);
        CheckAlternateGroups(run);
        await CheckByProductsAsync(run);
        await CheckForCyclesAsync(run);
        DecideTheOutcome(run);
    }

    private async Task<bool> ReadTheBillAsync(Run run)
    {
        var a = run.A;
        var bill = await reader.BillAsync(Pic.X(a.Company, 10), Pic.X(a.Site, 10), a.BomId, 500, run.Ct);
        run.Bill = bill;
        if (bill.Status is "OK" or "TOOMANY")
        {
            a.BomRef = bill.Header.BomRef;
            a.BomRev = bill.Header.BomRev;
            a.ParentItem = bill.Header.ParentItem;
            a.ComponentCount = bill.LineCount;
            if (bill.Status == "TOOMANY") AddFinding(run, "ERROR", "BOM0302", bill.Message, 0, 0);
            return true;
        }
        if (bill.Status == "SQLFAIL")
        {
            a.ReturnCode = 12;
            a.Reason = "SQL0001";
        }
        else
        {
            a.ReturnCode = 16;
            a.Reason = "BOM0303";
        }
        a.Message = Pic.X(bill.Message, 200);
        return false;
    }

    // ------------------------------------------------------------------ BOM-001 header rules

    private async Task CheckTheHeaderAsync(Run run)
    {
        var h = run.Bill.Header;
        if (h.NominalBasis <= 0) AddFinding(run, "ERROR", "BOM0310", "The nominal output basis must be positive", 0, 0);
        if (h.Yield <= 0 || h.Yield > 1) AddFinding(run, "ERROR", "BOM0311", "Yield must be above zero and not more than one", 0, 0);
        if (h.Quantum <= 0) AddFinding(run, "ERROR", "BOM0312", "The production quantum must be positive", 0, 0);
        if (h.EffTo != 0 && h.EffTo <= h.EffFrom) AddFinding(run, "ERROR", "BOM0313", "Effectivity end cannot precede or equal its start", 0, 0);
        if (run.Bill.LineCount == 0) AddFinding(run, "ERROR", "BOM0314", "A bill with no component occurrence cannot be used", 0, 0);
        if (h.IsPhantom == "Y" && h.Yield != 1) AddFinding(run, "ERROR", "BOM0315", "A phantom has yield one and no independent loss", 0, 0);
        await CheckParentClassAsync(run);
        CheckLossIntent(run);
    }

    /// <summary>The parent must be a manufactured item of the item master (SQL failure: no finding).</summary>
    private async Task CheckParentClassAsync(Run run)
    {
        var parent = run.Bill.Header.ParentItem;
        string? flag;
        try
        {
            flag = await sql.WithReaderAsync(BomSql.Q(ParentClassSql), BomSql.Args(Pic.X(run.A.Company, 10), parent), async r =>
                await r.ReadAsync(run.Ct) ? BomSql.Text(r, 0, 12) : null, run.Ct);
        }
        catch (SqlioException)
        {
            return;
        }
        if (flag is null)
        {
            AddFinding(run, "ERROR", "BOM0317", Pic.Str(120, "Parent ", Pic.T(parent), " is not held in the item master"), 0, 0);
            return;
        }
        if (!flag.StartsWith('Y'))
        {
            AddFinding(run, "ERROR", "BOM0317", Pic.Str(120, "Parent ", Pic.T(parent), " is not classified as a manufactured item"), 0, 0);
        }
    }

    /// <summary>Scrap versus yield: both are allowed only where the bill states the intent.</summary>
    private static void CheckLossIntent(Run run)
    {
        var h = run.Bill.Header;
        if (h.Yield >= 1 || !Pic.Blank(h.LossIntent)) return;
        if (!run.Bill.Lines.Any(l => l.ScrapPct > 0)) return;
        AddFinding(run, "WARNING", "BOM0316", "Yield and scrap are both used - state the intent so ", 0, 0);
    }

    // ------------------------------------------------------------------ component rules

    private async Task CheckEveryComponentAsync(Run run)
    {
        for (var i = 1; i <= run.Bill.LineCount; i++)
        {
            await CheckOneComponentAsync(run, i, run.Bill.Lines[i - 1].LineNum);
        }
    }

    private async Task CheckOneComponentAsync(Run run, int i, int j)
    {
        var l = run.Bill.Lines[i - 1];
        if (l.ItemFound != "Y")
        {
            AddFinding(run, "ERROR", "BOM0320", Pic.Str(120, "Component ", Pic.T(l.Item), " is not held in the item master"), j, i);
            return;
        }
        if (l.ConvFact <= 0) AddFinding(run, "ERROR", "BOM0321", "A positive unit conversion factor is required", j, i);
        if (Pic.Blank(l.StockUom)) AddFinding(run, "ERROR", "BOM0322", "The component item has no stock unit", j, i);
        if (l.ScrapPct < 0) AddFinding(run, "ERROR", "BOM0323", "A scrap allowance cannot be negative", j, i);
        if (l.UsageQty < 0) AddFinding(run, "ERROR", "BOM0324", "Negative usage is not a by-product mechanism", j, i);
        if (l.UsageQty == 0 && l.Supply != "NOTE") AddFinding(run, "ERROR", "BOM0325", "A zero quantity line is permitted only as a note", j, i);
        if (l.Supply == "COSTONLY" && l.CostRate <= 0) AddFinding(run, "ERROR", "BOM0326", "A cost-only resource needs a rate to be complete", j, i);
        if (l.Supply == "MAKE") await CheckChildBillExistsAsync(run, l, i, j);
        if (l.Supply == "PHANTOM") await CheckPhantomChildAsync(run, l, i, j);
        if (l.EffTo != 0 && l.EffFrom != 0 && l.EffTo <= l.EffFrom)
        {
            AddFinding(run, "ERROR", "BOM0327", "Line effectivity end precedes or equals its start", j, i);
        }
    }

    private SelectArea NewSelect(Run run, BillLine l) => new()
    {
        Function = "SELECT",
        Company = Pic.X(run.A.Company, 10),
        Site = Pic.X(run.A.Site, 10),
        User = Pic.X(run.A.User, 20),
        BusinessDate = run.A.EffDate,
        ParentItem = l.Item,
        Purpose = "STANDARD",
        EffDate = run.A.EffDate,
    };

    private async Task CheckChildBillExistsAsync(Run run, BillLine l, int i, int j)
    {
        var s = NewSelect(run, l);
        s.ParentRev = l.Rev;
        await selector.CallAsync(s, run.Ct);
        if (s.ReturnCode <= 4 && s.BomId != 0) return;
        AddFinding(run, "ERROR", "BOM0328", Pic.Str(120, "No usable bill for the manufactured component ", Pic.T(l.Item)), j, i);
    }

    private async Task CheckPhantomChildAsync(Run run, BillLine l, int i, int j)
    {
        var s = NewSelect(run, l);
        await selector.CallAsync(s, run.Ct);
        if (s.ReturnCode > 4 || s.BomId == 0)
        {
            AddFinding(run, "ERROR", "BOM0329", Pic.Str(120, "Phantom ", Pic.T(l.Item), " has no bill to expand through"), j, i);
            return;
        }
        if (s.IsPhantom != "Y") AddFinding(run, "WARNING", "BOM0330", "This line expands a bill that is not marked phantom", j, i);
    }

    /// <summary>Every alternate group needs exactly one selected choice; the first line of the group reports.</summary>
    private static void CheckAlternateGroups(Run run)
    {
        var lines = run.Bill.Lines;
        for (var i = 1; i <= lines.Count; i++)
        {
            var g = lines[i - 1].AltGroup;
            if (Pic.Blank(g)) continue;
            int chosen = 0, first = 0;
            for (var k = 1; k <= lines.Count; k++)
            {
                if (lines[k - 1].AltGroup != g) continue;
                if (first == 0) first = k;
                if (lines[k - 1].Selected == "Y") chosen++;
            }
            if (first != i) continue;
            var j = lines[i - 1].LineNum;
            if (chosen == 0)
            {
                AddFinding(run, "ERROR", "BOM0331", Pic.Str(120, "Alternate group ", Pic.T(g), " has no selected choice"), j, i);
                continue;
            }
            if (chosen > 1)
            {
                AddFinding(run, "ERROR", "BOM0332", Pic.Str(120, "Alternate group ", Pic.T(g), " has more than one selected choice"), j, i);
            }
        }
    }

    /// <summary>A recovery must be positive and carry an approved valuation (SQL failure: no finding).</summary>
    private async Task CheckByProductsAsync(Run run)
    {
        string? count;
        try
        {
            count = await sql.WithReaderAsync(BomSql.Q(ByProductSql), BomSql.Args(run.A.BomId), async r =>
                await r.ReadAsync(run.Ct) ? BomSql.Text(r, 0, 12) : null, run.Ct);
        }
        catch (SqlioException)
        {
            return;
        }
        if (count is null) return;
        if (BomSql.NumVal(count) > 0) AddFinding(run, "ERROR", "BOM0333", "A recovery must be positive and approved", 0, 0);
    }

    private async Task CheckForCyclesAsync(Run run)
    {
        var a = run.A;
        var h = run.Bill.Header;
        var c = new CycleArea
        {
            Function = "CHECK",
            Company = Pic.X(a.Company, 10),
            Site = Pic.X(a.Site, 10),
            User = Pic.X(a.User, 20),
            BusinessDate = a.EffDate,
            RootItem = h.ParentItem,
            RootRev = h.ParentRev,
            Purpose = h.Purpose,
            Variant = h.Variant,
            EffDate = a.EffDate,
            MaxDepth = 25,
            IncludeOpt = "Y",
        };
        await cycles.CallAsync(c, run.Ct);
        if (c.CycleFound != "Y") return;
        a.CycleFound = "Y";
        a.CyclePath = Pic.X(c.CyclePath, 200);
        // Ws-I is left one past the last line by the alternate-group loop
        AddFinding(run, "ERROR", "BOM0334", Pic.Str(120, "Cyclic structure: ", Pic.T(c.CyclePath)), c.CycleLine, run.Bill.LineCount + 1);
    }

    /// <summary>
    /// Add-Finding: counts every finding, keeps at most BV-FIND-MAX; a header finding (line 0) names the parent and the
    /// bill reference, a line finding names BC-ITEM(Ws-I) and the path "parent/line:item".
    /// </summary>
    private static void AddFinding(Run run, string severity, string reason, string text, int j, int i)
    {
        var a = run.A;
        if (severity == "ERROR") run.Errors = Pic.I(run.Errors + 1, 4);
        else run.Warnings = Pic.I(run.Warnings + 1, 4);
        if (a.Findings.Count >= a.FindMax) return;
        string item, path;
        if (j == 0)
        {
            item = run.Bill.Header.ParentItem;
            path = Pic.X(a.BomRef, 80);
        }
        else
        {
            item = ItemAt(run, i);
            path = Pic.Str(80, Pic.T(run.Bill.Header.ParentItem), "/", Pic.Z(j), ":", Pic.T(item));
        }
        a.Findings.Add(new ValidationFinding
        {
            Severity = Pic.X(severity, 8),
            Reason = Pic.X(reason, 8),
            LineNum = Pic.I(j, 4),
            Item = Pic.X(item, 40),
            Path = path,
            Message = Pic.X(text, 120),
        });
    }

    /// <summary>
    /// BC-ITEM(Ws-I). The cycle finding reads the entry one past the last line; the bill buffer is a per-call local (spec
    /// CS-BOM-09), so that entry is spaces.
    /// </summary>
    private static string ItemAt(Run run, int i) => i >= 1 && i <= run.Bill.LineCount ? run.Bill.Lines[i - 1].Item : "";

    private static void DecideTheOutcome(Run run)
    {
        var a = run.A;
        a.ErrorCount = run.Errors;
        a.WarningCount = run.Warnings;
        if (run.Errors > 0)
        {
            a.Outcome = "FAIL";
            a.ReleaseOk = "N";
            a.ReturnCode = 8;
            a.Reason = "BOM0340";
            a.Message = Pic.Str(200, "Validation failed with ", Pic.Z(Pic.I(run.Errors, 4)), " error(s) - this bill cannot be released");
            a.MessageCount = Pic.I(a.MessageCount + 1, 2);
            return;
        }
        if (run.Warnings > 0)
        {
            a.Outcome = "WARNING";
            a.ReleaseOk = "Y";
            a.ReturnCode = 4;
            a.Reason = "BOM0341";
            a.Message = "Validation passed with warnings to review";
            a.MessageCount = Pic.I(a.MessageCount + 1, 2);
            return;
        }
        a.Outcome = "PASS";
        a.ReleaseOk = "Y";
        a.ReturnCode = 0;
        a.Message = "Validation complete - the bill may be released";
    }
}
