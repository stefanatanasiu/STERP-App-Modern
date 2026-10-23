using Sterp.Modules.Bom.Contracts;
using Sterp.Modules.Cst.Data;
using Sterp.Modules.Cst.Domain;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Cst.Engines;

/// <summary>
/// COSTROLL (cobol/online/cst/COSTROLL.cob) - the cost roll-up through the engineering explosion: ROLLUP (1000-1300 then the
/// first window, 2000) and GETLINE (2000), calling BOMCOST through the BOM public contract IC-BOM-BOMCOST on the caller's unit
/// of work and, when a work centre is named (never on a production route, F-COSTROLL-16), LABOVH OVERHEAD. WS-BC-CA - the
/// BOMCOST area GETLINE pages - lives exactly as long as this instance (one request, CS-01 / BC-CST-03).
/// SAVESET and EXPLAIN have no production route (D-CST-04, SME-789). Writes nothing.
/// </summary>
internal sealed class CostRollEngine(IBomCostEngine bomCost, LabOvhEngine labovh)
{
    /// <summary>WS-BC-CA.</summary>
    private BomCostArea _bc = new();

    public async Task LinkAsync(CostRollArea a, CancellationToken ct)
    {
        var ca = a.Ca;
        Initialise(a);
        if (Validate(a)) return;
        switch (ca.Function)
        {
            case "ROLLUP":
                await RollUpAsync(a, ct);
                break;
            case "GETLINE":
                await GetLinesAsync(a, ct);
                break;
            case "SAVESET" or "EXPLAIN":
                throw new NotSupportedException($"COSTROLL {ca.Function} has no production route (decision D-CST-04).");
            default:
                ca.Reject("CST0608", "Function is not supported by COSTROLL");
                break;
        }
    }

    /// <summary>0100-INITIALISE.</summary>
    private static void Initialise(CostRollArea a)
    {
        a.Ca.Reset("COSTROLL", "CST-010");
        a.GrossStarts = 0;
        a.GoodOutput = 0;
        a.MaterialCost = 0;
        a.LabourCost = 0;
        a.MachineCost = 0;
        a.SetupCost = 0;
        a.SubconCost = 0;
        a.OverheadCost = 0;
        a.OtherCost = 0;
        a.RecoveryCredit = 0;
        a.TotalCost = 0;
        a.UnitCost = 0;
        a.BranchValue = 0;
        a.OccCount = 0;
        a.MissingCount = 0;
        a.SetWritten = 0;
        a.LineCnt = 0;
        a.MoreLines = "N";
        a.Completeness = "COMPLETE";
        a.ErrorPath = "";
        a.Warning = "";
        a.ItemCode = Pic.Upper(a.ItemCode);
        a.Boundary = Pic.Upper(a.Boundary);
        if (Pic.Blank(a.Boundary)) a.Boundary = "EXPAND";
        if (Pic.Blank(a.Currency)) a.Currency = "GBP";
        if (a.LotSize == 0) a.LotSize = Pic.S(1m, 6);
        if (a.EffDate == 0) a.EffDate = a.Ca.BusinessDate;
        if (a.CostDate == 0) a.CostDate = a.EffDate;
        if (a.WinMax <= 0 || a.WinMax > 20) a.WinMax = 20;
    }

    /// <summary>0200-VALIDATE: the first refusal in coded order wins.</summary>
    private static bool Validate(CostRollArea a)
    {
        if (Pic.Blank(a.Ca.Company) || Pic.Blank(a.ItemCode))
        {
            a.Ca.Reject("CST0601", "A company and an item code are required");
            return true;
        }
        if (a.LotSize <= 0)
        {
            a.Ca.Reject("CST0602", "The costing lot size must be positive - a fixed setup charge needs a denominator");
            return true;
        }
        if (!Pic.Eq(a.Boundary, "EXPAND") && !Pic.Eq(a.Boundary, "STOP"))
        {
            a.Ca.Reject("CST0606", "The valuation boundary must be EXPAND or STOP");
            return true;
        }
        return false;
    }

    private static bool StopAtSub(CostRollArea a) => Pic.Eq(a.Boundary, "STOP");

    /// <summary>1000-ROLL-UP.</summary>
    private async Task RollUpAsync(CostRollArea a, CancellationToken ct)
    {
        await CallBomCostAsync(a, ct);
        if (a.Ca.ReturnCode > 4) return;
        await ConversionCostAsync(a, ct);
        Totals(a);
        await GetLinesAsync(a, ct);
    }

    /// <summary>1100-CALL-BOMCOST: BOMCOST COST for the STANDARD engineering bill at the lot size (STOP = depth 1).</summary>
    private async Task CallBomCostAsync(CostRollArea a, CancellationToken ct)
    {
        var ca = a.Ca;
        _bc = new BomCostArea
        {
            Program = "BOMCOST",
            Function = "COST",
            Company = ca.Company,
            Site = ca.Site,
            User = ca.User,
            BusinessDate = ca.BusinessDate,
            ParentItem = a.ItemCode,
            ParentRev = a.Revision,
            Purpose = "STANDARD",
            ReqQty = Pic.N(a.LotSize, 15, 6),
            EffDate = a.EffDate,
            CostDate = a.CostDate,
            CostSet = a.CostSet,
            Currency = a.Currency,
            Netted = a.Netted,
            MaxDepth = StopAtSub(a) ? 1 : a.MaxDepth,
        };
        await bomCost.LinkAsync(_bc, ct);
        var r = _bc.Result;
        a.ErrorPath = Pic.X(_bc.ErrorPath, 200);
        if (r.ReturnCode > 4)
        {
            ca.ReasonCode = "CST0603";
            ca.Message = Pic.X(r.Message, 200);
            ca.ReturnCode = r.ReturnCode;
            a.Completeness = "INCOMPLETE";
            return;
        }
        a.GrossStarts = Pic.N(_bc.GrossStarts, 13, 6);
        a.MaterialCost = Pic.N(_bc.MaterialCost, 13, 2);
        a.SubconCost = Pic.N(_bc.ServiceCost, 13, 2);
        a.SetupCost = Pic.N(_bc.SetupCost, 13, 2);
        a.RecoveryCredit = Pic.N(_bc.RecoveryCredit, 13, 2);
        a.OccCount = Pic.I(_bc.OccCount, 9);
        a.MissingCount = Pic.I(_bc.MissingRates, 4);
        a.GoodOutput = Pic.N(_bc.UnitDenom, 13, 6);
        a.BranchValue = Pic.N(_bc.TotalCost, 13, 2);
        if (!Pic.Eq(_bc.Completeness, "COMPLETE")) a.Completeness = Pic.X(_bc.Completeness, 12);
        if (a.GoodOutput <= 0) a.GoodOutput = a.LotSize;
        if (a.MissingCount > 0)
        {
            a.Completeness = "INCOMPLETE";
            ca.ReasonCode = "CST0604";
            a.Warning = "Some components have no cost in this set";
            if (ca.ReturnCode == 0) ca.ReturnCode = 4;
        }
    }

    /// <summary>1200-CONVERSION-COST: labour = BOMCOST conversion - setup; LABOVH OVERHEAD when a work centre is named.</summary>
    private async Task ConversionCostAsync(CostRollArea a, CancellationToken ct)
    {
        var ca = a.Ca;
        a.LabourCost = ((Cd)_bc.ConversionCost - _bc.SetupCost).Store(13, 2);
        if (Pic.Blank(a.WorkCentre)) return;
        var lo = new LabOvhArea
        {
            Ca = CaHeader.Initialized("OVERHEAD", ca.Company, ca.Site, ca.User, ca.BusinessDate),
            WorkCentre = Pic.X(a.WorkCentre, 20),
            EffDate = a.EffDate,
            ItemCode = a.ItemCode,
            GrossUnits = a.GrossStarts,
            RunCount = 1,
            BaseCost = a.LabourCost,
            BaseExclOvhd = "Y",
        };
        lo.Ca.Program = "LABOVH";
        await labovh.LinkAsync(lo, ct);
        if (lo.Ca.ReturnCode > 4)
        {
            ca.ReasonCode = "CST0605";
            a.Warning = Pic.X(lo.Ca.Message, 80);
            a.Completeness = "INCOMPLETE";
            if (ca.ReturnCode == 0) ca.ReturnCode = 4;
            return;
        }
        a.OverheadCost = lo.OvhdCost;
        a.MachineCost = ((Cd)a.MachineCost + lo.MachineCost).Store(13, 2);
    }

    /// <summary>1300-TOTALS: the element equation (recovery subtracted as a credit), unit ROUNDED on the good output.</summary>
    private static void Totals(CostRollArea a)
    {
        var ca = a.Ca;
        a.TotalCost = ((Cd)a.MaterialCost + a.LabourCost + a.MachineCost + a.SetupCost + a.SubconCost + a.OverheadCost + a.OtherCost
                       - a.RecoveryCredit).Store(13, 2);
        if (a.TotalCost < 0)
        {
            ca.ReasonCode = "CST0609";
            ca.Message = "The recovery credit drives the cost below zero - the controller must review it";
            ca.ReturnCode = 6;
            ca.CountMessage();
        }
        if (a.GoodOutput > 0) a.UnitCost = ((Cd)a.TotalCost / a.GoodOutput).Store(11, 6, rounded: true, previous: a.UnitCost);
        a.BranchValue = a.TotalCost;
    }

    /// <summary>2000-GET-LINES / 2100-BOUNDARY-FLAG: one BOMCOST GETLINE window of 25 from CR-FROM-LINE, at most CR-WIN-MAX copied.</summary>
    private async Task GetLinesAsync(CostRollArea a, CancellationToken ct)
    {
        a.LineCnt = 0;
        a.MoreLines = "N";
        _bc.FromLine = a.FromLine;
        _bc.WinMax = 25;
        _bc.Function = "GETLINE";
        await bomCost.LinkAsync(_bc, ct);
        if (_bc.Result.ReturnCode > 4) return;
        a.MoreLines = _bc.MoreLines;
        for (var i = 0; i < _bc.WinCnt && a.LineCnt < a.WinMax; i++)
        {
            var b = _bc.Lines[i];
            a.LineCnt++;
            var l = a.Lines[a.LineCnt - 1];
            l.Seq = Pic.I(b.Seq, 9);
            l.Depth = Pic.I(b.Depth, 4);
            l.Item = Pic.X(b.Item, 40);
            l.Rev = Pic.X(b.Rev, 4);
            l.Desc = Pic.X(b.Desc, 40);
            l.Element = Pic.X(b.Element, 10);
            l.Source = Pic.X(b.Source, 12);
            l.ReqQty = Pic.N(b.ReqQty, 13, 6);
            l.UnitCost = Pic.N(b.UnitCost, 11, 6);
            l.ExtCost = Pic.N(b.ExtCost, 13, 2);
            l.Warning = Pic.X(b.Warning, 60);
            l.Boundary = b.Branch != 0 ? "BRANCH" : "LEAF";
            if (StopAtSub(a) && b.Depth > 1)
            {
                l.Contrib = "N";
                l.Boundary = "STOPPED";
            }
            else
            {
                l.Contrib = b.Branch != 0 && b.ExtCost == 0 ? "N" : "Y";
            }
        }
        if (_bc.WinCnt > a.WinMax) a.MoreLines = "Y";
    }
}

/// <summary>
/// LABOVH (cobol/online/cst/LABOVH.cob) - OVERHEAD (3000, with the 1000 rate read and the 4000 absorption it performs), the
/// only function COSTROLL calls (and only for a named work centre, which no production route supplies: dormant path).
/// </summary>
internal sealed class LabOvhEngine(ISqlRunner sql)
{
    public async Task LinkAsync(LabOvhArea a, CancellationToken ct)
    {
        var ca = a.Ca;
        var db = new CstSqlio(sql, ca);
        var runs = Initialise(a);
        if (Validate(a)) return;
        switch (ca.Function)
        {
            case "OVERHEAD":
                await OverheadAsync(a, db, runs, ct);
                break;
            case "RATES" or "LABOUR" or "ABSORB" or "OVHRATE":
                throw new NotSupportedException($"LABOVH {ca.Function} has no production route (decision D-CST-04).");
            default:
                ca.Reject("CST0508", Pic.Str(200, "Function ", Pic.T(ca.Function), " is not supported by LABOVH"));
                break;
        }
    }

    private static int Initialise(LabOvhArea a)
    {
        a.Ca.Reset("LABOVH", "CST-011");
        a.SetupCost = 0;
        a.RunCost = 0;
        a.LabourCost = 0;
        a.MachineCost = 0;
        a.OvhdCost = 0;
        a.TotalCost = 0;
        a.UnitCost = 0;
        a.TotalHours = 0;
        a.DerivedRate = 0;
        a.AbsorbedGbp = 0;
        a.RecoveryGbp = 0;
        a.CapacityPct = 0;
        a.UnderRecovered = "N";
        a.Warning = "";
        a.BasisText = "";
        a.WorkCentre = Pic.Upper(a.WorkCentre);
        if (a.EffDate == 0) a.EffDate = a.Ca.BusinessDate;
        if (a.RunCount <= 0) a.RunCount = 1;
        if (Pic.Blank(a.RateBasis)) a.RateBasis = "UNIT";
        return a.RunCount;
    }

    private static bool Validate(LabOvhArea a)
    {
        if (Pic.Blank(a.Ca.Company))
        {
            a.Ca.Reject("CST0501", "A company is required");
            return true;
        }
        if (a.SetupHours < 0 || a.RunHoursUnit < 0 || a.MachineHrsUnit < 0)
        {
            a.Ca.Reject("CST0503", "Hours may not be negative");
            return true;
        }
        if (a.RunCount <= 0)
        {
            a.Ca.Reject("CST0504", "The execution run count must be positive");
            return true;
        }
        return false;
    }

    /// <summary>1000-READ-CENTRE: the latest active rates on or before the effective date.</summary>
    private static async Task ReadCentreAsync(LabOvhArea a, CstSqlio db, CancellationToken ct)
    {
        if (Pic.Blank(a.WorkCentre)) return;
        await db.SingleAsync(
            "SELECT TOP 1 SetupRatePerHour, RunRatePerHour, MachineRatePerHour, OverheadRatePerHour, OverheadBasis, " +
            "ISNULL(NormalCapacityHours,0) FROM cst.WorkCentre WHERE CompanyCode = ? AND WorkCentreCode = ? AND EffectiveFrom <= ? AND " +
            "IsActive = 1 ORDER BY EffectiveFrom DESC", 6,
            [SqlioParm.Text(a.Ca.Company), SqlioParm.Text(a.WorkCentre), SqlioParm.Digits(a.EffDate, 8)], ct);
        if (db.SqlFailed) return;
        if (!db.RowFound)
        {
            a.Ca.ReasonCode = "CST0502";
            a.Ca.Message = Pic.Str(200, "Work centre ", Pic.T(a.WorkCentre), " has no rates effective at that date");
            a.Ca.ReturnCode = 16;
            return;
        }
        var row = db.Row;
        if (a.SetupRate == 0) a.SetupRate = Pic.N(row.NumVal(0), 11, 6);
        if (a.RunRate == 0) a.RunRate = Pic.N(row.NumVal(1), 11, 6);
        if (a.MachineRate == 0) a.MachineRate = Pic.N(row.NumVal(2), 11, 6);
        if (a.OvhdRate == 0) a.OvhdRate = Pic.N(row.NumVal(3), 11, 6);
        if (Pic.Blank(a.OvhdBasis)) a.OvhdBasis = Pic.X(row.Raw(4), 10);
        if (a.NormalCapHrs == 0) a.NormalCapHrs = Pic.N(row.NumVal(5), 11, 6);
    }

    /// <summary>3000-OVERHEAD.</summary>
    private static async Task OverheadAsync(LabOvhArea a, CstSqlio db, int runs, CancellationToken ct)
    {
        await ReadCentreAsync(a, db, ct);
        if (a.Ca.ReturnCode > 4) return;
        switch (Pic.X(a.OvhdBasis, 10))
        {
            case "LABOURHR":
                if (a.TotalHours == 0) a.TotalHours = ((Cd)a.SetupHours * runs + (Cd)a.RunHoursUnit * a.GrossUnits).Store(11, 6);
                a.OvhdCost = ((Cd)a.TotalHours * a.OvhdRate).Store(13, 2, rounded: true);
                // STRING FUNCTION TRIM(LO-BASIS-TEXT) "capacity": the trailing space of the first literal is trimmed away
                a.BasisText = Pic.X("Rate per direct labour hour at normalcapacity", 60);
                break;
            case "MACHINEHR":
            {
                var calc3 = ((Cd)a.MachineHrsUnit * a.GrossUnits).Store(13, 12);
                a.OvhdCost = ((Cd)calc3 * a.OvhdRate).Store(13, 2, rounded: true);
                a.TotalHours = Pic.N(calc3, 11, 6);
                a.BasisText = "Rate per machine hour at normal capacity";
                break;
            }
            case "PCTCOST":
            {
                var wsBase = a.BaseCost;
                if (a.BaseExclOvhd == "Y") wsBase = ((Cd)a.BaseCost - a.EmbeddedOvhd).Store(13, 2);
                if (wsBase < 0)
                {
                    a.Ca.Reject("CST0507", "The overhead base is negative onceembedded overhead is removed");
                    return;
                }
                a.OvhdCost = ((Cd)wsBase * a.OvhdPct / 100).Store(13, 2, rounded: true);
                a.BasisText = "Percentage of a base that excludes overhead";
                break;
            }
            default:
                a.Ca.Reject("CST0506", Pic.Str(200, "Overhead basis ", Pic.T(a.OvhdBasis), " is not recognised"));
                return;
        }
        a.TotalCost = ((Cd)a.LabourCost + a.MachineCost + a.OvhdCost).Store(13, 2);
        if (a.GrossUnits > 0) a.UnitCost = ((Cd)a.TotalCost / a.GrossUnits).Store(11, 6, rounded: true, previous: a.UnitCost);
        Absorption(a);
    }

    /// <summary>4000-ABSORPTION.</summary>
    private static void Absorption(LabOvhArea a)
    {
        if (a.NormalCapHrs <= 0) return;
        if (a.ActualHours == 0) a.ActualHours = a.TotalHours;
        a.CapacityPct = ((Cd)a.ActualHours * 100 / a.NormalCapHrs).Store(5, 4, rounded: true, previous: a.CapacityPct);
        a.AbsorbedGbp = ((Cd)a.ActualHours * a.OvhdRate).Store(13, 2, rounded: true);
        if (a.IncurredGbp == 0) a.IncurredGbp = ((Cd)a.NormalCapHrs * a.OvhdRate).Store(13, 2, rounded: true);
        a.RecoveryGbp = ((Cd)a.AbsorbedGbp - a.IncurredGbp).Store(13, 2);
        if (a.RecoveryGbp < 0)
        {
            a.UnderRecovered = "Y";
            a.Warning = "Under recovered overhead is a period expense";
            if (a.Ca.ReturnCode == 0)
            {
                a.Ca.ReturnCode = 4;
                a.Ca.ReasonCode = "CST0026";
            }
        }
    }
}

/// <summary>
/// WHATIF (cobol/online/cst/WHATIF.cob) - scenario costing: SCENARIO (1000) and MARGINWI (3000, with the 2000 comparison it
/// performs). Every run is a fresh COSTROLL ROLLUP with the target set cleared; no transaction is opened and nothing is
/// written (CST-023). The entry function COMPARE has no production route (D-CST-04).
/// </summary>
internal sealed class WhatIfEngine(CostRollEngine roll, MarginEngine margin)
{
    private sealed class Run
    {
        public string Set { get; set; } = "";
        public decimal Lot { get; set; }
        public string Ctr { get; set; } = "";
        public string Bound { get; set; } = "";
        public decimal MatFactor { get; set; } = 1;
        public decimal RateFactor { get; set; } = 1;
        public decimal Conversion { get; set; }
        public decimal Overhead { get; set; }
        public decimal Material { get; set; }
        public decimal Total { get; set; }
        public decimal Unit { get; set; }
        public CostRollArea Roll { get; set; } = new();
    }

    public async Task LinkAsync(WhatIfArea a, CancellationToken ct)
    {
        var ca = a.Ca;
        Initialise(a);
        if (Validate(a)) return;
        var run = new Run();
        switch (ca.Function)
        {
            case "SCENARIO":
                await ScenarioAsync(a, run, ct);
                break;
            case "MARGINWI":
                await MarginWhatIfAsync(a, run, ct);
                break;
            case "COMPARE":
                throw new NotSupportedException("WHATIF COMPARE has no production route as an entry function (decision D-CST-04).");
            default:
                ca.Reject("CST1206", Pic.Str(200, "Function ", Pic.T(ca.Function), " is not supported by WHATIF"));
                break;
        }
    }

    /// <summary>0100-INITIALISE.</summary>
    private static void Initialise(WhatIfArea a)
    {
        a.Ca.Reset("WHATIF", "CST-023");
        a.Warning = "";
        a.BaseTotal = 0;
        a.BaseUnit = 0;
        a.BestVariant = 0;
        a.BaseComplete = "";
        a.SideEffects = "N";
        if (a.BaseLotSize == 0) a.BaseLotSize = Pic.S(1m, 6);
        if (a.VariantCnt < 0) a.VariantCnt = 0;
    }

    /// <summary>0200-VALIDATE.</summary>
    private static bool Validate(WhatIfArea a)
    {
        var ca = a.Ca;
        if (Pic.Blank(ca.Company) || Pic.Blank(a.ItemCode))
        {
            ca.Reject("CST1201", "A company and an item are required");
            return true;
        }
        if (a.VariantCnt > 6)
        {
            ca.Reject("CST1205", "A scenario carries at most six variants");
            return true;
        }
        if (a.BaseLotSize <= 0)
        {
            ca.Reject("CST1203", "The lot size must be positive");
            return true;
        }
        if (ca.Function is "COMPARE" or "MARGINWI" && a.VariantCnt < 1)
        {
            ca.Reject("CST1202", "At least one variant is required to compare");
            return true;
        }
        return false;
    }

    /// <summary>1000-SCENARIO: the base case, then each variant.</summary>
    private async Task ScenarioAsync(WhatIfArea a, Run run, CancellationToken ct)
    {
        await RunBaseAsync(a, run, ct);
        for (var v = 1; v <= a.VariantCnt; v++) await RunVariantAsync(a, run, v, ct);
    }

    /// <summary>1100-RUN-BASE.</summary>
    private async Task RunBaseAsync(WhatIfArea a, Run run, CancellationToken ct)
    {
        run.Set = a.BaseCostSet;
        run.Lot = a.BaseLotSize;
        run.Ctr = a.BaseWorkCtr;
        run.Bound = "EXPAND";
        run.MatFactor = 1;
        run.RateFactor = 1;
        await RollAsync(a, run, ct);
        a.BaseTotal = run.Total;
        a.BaseUnit = run.Unit;
        a.BaseComplete = run.Roll.Completeness;
        if (!Pic.Eq(run.Roll.Completeness, "COMPLETE"))
        {
            a.Ca.ReasonCode = "CST1204";
            a.Warning = Pic.Str(80, "Base case is ", Pic.T(run.Roll.Completeness), " - compare the variants with that in mind");
            a.Ca.ReturnCode = 4;
        }
    }

    /// <summary>1200-RUN-VARIANT.</summary>
    private async Task RunVariantAsync(WhatIfArea a, Run run, int v, CancellationToken ct)
    {
        var w = a.Variants[v - 1];
        run.Set = a.BaseCostSet;
        run.Lot = a.BaseLotSize;
        run.Ctr = a.BaseWorkCtr;
        run.Bound = "EXPAND";
        run.MatFactor = 1;
        run.RateFactor = 1;
        if (!Pic.Blank(w.CostSet)) run.Set = w.CostSet;
        if (w.LotSize > 0) run.Lot = w.LotSize;
        if (!Pic.Blank(w.WorkCtr)) run.Ctr = w.WorkCtr;
        if (!Pic.Blank(w.Boundary)) run.Bound = w.Boundary;
        if (w.MatFactor != 0) run.MatFactor = w.MatFactor;
        if (w.RateFactor != 0) run.RateFactor = w.RateFactor;
        if (Pic.Blank(w.Label)) w.Label = Pic.Str(30, "Variant ", v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await RollAsync(a, run, ct);
        w.Material = run.Material;
        w.Conversion = run.Conversion;
        w.Overhead = run.Overhead;
        w.Total = run.Total;
        w.Unit = run.Unit;
        w.Complete = run.Roll.Completeness;
        w.Warning = Pic.X(run.Roll.Warning, 60);
        w.Diff = ((Cd)run.Total - a.BaseTotal).Store(13, 2);
        w.DiffPct = a.BaseTotal != 0
            ? (((Cd)run.Total - a.BaseTotal) * 100 / a.BaseTotal).Store(7, 4, rounded: true, previous: w.DiffPct)
            : 0;
        w.MarginPct = 0;
    }

    /// <summary>2000-COMPARE: the scenario, then the cheapest COMPLETE variant with a positive unit.</summary>
    private async Task CompareAsync(WhatIfArea a, Run run, CancellationToken ct)
    {
        await ScenarioAsync(a, run, ct);
        a.BestVariant = 0;
        var bestUnit = 0m;
        for (var v = 1; v <= a.VariantCnt; v++)
        {
            var w = a.Variants[v - 1];
            if (Pic.Eq(w.Complete, "COMPLETE") && w.Unit > 0 && (a.BestVariant == 0 || w.Unit < bestUnit))
            {
                a.BestVariant = v;
                bestUnit = w.Unit;
            }
        }
        if (a.BestVariant == 0)
        {
            a.Warning = "No complete variant to recommend";
            a.Ca.ReturnCode = 4;
        }
    }

    /// <summary>3000-MARGINWI.</summary>
    private async Task MarginWhatIfAsync(WhatIfArea a, Run run, CancellationToken ct)
    {
        await CompareAsync(a, run, ct);
        if (a.TargetPrice == 0)
        {
            a.Warning = "No target price - margin is undefined";
            a.Ca.ReturnCode = 4;
            return;
        }
        for (var v = 1; v <= a.VariantCnt; v++) await OneMarginAsync(a, v, ct);
    }

    /// <summary>3100-ONE-MARGIN: MARGIN EXPECTED for one unit at the target price, cost = the variant unit.</summary>
    private async Task OneMarginAsync(WhatIfArea a, int v, CancellationToken ct)
    {
        var w = a.Variants[v - 1];
        var mg = new MarginArea
        {
            Ca = CaHeader.Initialized("EXPECTED", a.Ca.Company, "", a.Ca.User, 0),
            DocType = "SCENARIO",
            DocRef = Pic.X(a.ScenarioRef, 40),
            LineNum = v,
            ItemCode = a.ItemCode,
            Quantity = Pic.S(1m, 6),
            UnitPrice = a.TargetPrice,
            RevenueInclVat = "N",
            CostBasis = "QUOTE",
            UnitCost = Pic.N(w.Unit, 11, 6),
            CostAmount = Pic.N(w.Unit, 13, 2),
        };
        await margin.LinkAsync(mg, ct);
        if (mg.Ca.ReturnCode > 4)
        {
            w.MarginPct = 0;
            w.Warning = "Margin could not be computed";
        }
        else if (Pic.Eq(mg.MarginState, "OK"))
        {
            w.MarginPct = Pic.N(mg.MarginPct, 7, 4);
        }
        else
        {
            w.MarginPct = 0;
            w.Warning = "Margin undefined at this price";
        }
    }

    /// <summary>4000-ROLL: one fresh COSTROLL ROLLUP (target set cleared), then the factors.</summary>
    private async Task RollAsync(WhatIfArea a, Run run, CancellationToken ct)
    {
        var r = new CostRollArea
        {
            Ca = CaHeader.Initialized("ROLLUP", a.Ca.Company, "", a.Ca.User, 0),
            ItemCode = a.ItemCode,
            Revision = a.Revision,
            CostSet = run.Set,
            TargetSet = "",
            EffDate = a.EffDate,
            CostDate = a.EffDate,
            LotSize = Pic.N(run.Lot, 13, 6),
            Boundary = Pic.X(run.Bound, 10),
            WorkCentre = run.Ctr,
            Currency = "GBP",
            FromLine = 1,
            WinMax = 20,
        };
        await roll.LinkAsync(r, ct);
        run.Roll = r;
        run.Material = r.MaterialCost;
        run.Conversion = ((Cd)r.LabourCost + r.MachineCost + r.SetupCost + r.SubconCost).Store(13, 2);
        run.Overhead = ((Cd)r.OverheadCost + r.RecoveryCredit).Store(13, 2);
        if (run.MatFactor != 1) run.Material = ((Cd)run.Material * run.MatFactor).Store(13, 2, rounded: true);
        if (run.RateFactor != 1)
        {
            run.Conversion = ((Cd)run.Conversion * run.RateFactor).Store(13, 2, rounded: true);
            run.Overhead = ((Cd)run.Overhead * run.RateFactor).Store(13, 2, rounded: true);
        }
        run.Total = ((Cd)run.Material + run.Conversion + run.Overhead + r.OtherCost).Store(13, 2);
        if (r.GoodOutput > 0) run.Unit = ((Cd)run.Total / r.GoodOutput).Store(11, 6, rounded: true, previous: run.Unit);
        else if (run.Lot > 0) run.Unit = ((Cd)run.Total / run.Lot).Store(11, 6, rounded: true, previous: run.Unit);
        else run.Unit = 0;
        if (r.Ca.ReturnCode > 4)
        {
            r.Completeness = "FAILED";
            r.Warning = Pic.X(r.Ca.Message, 80);
        }
    }
}

/// <summary>
/// MARGIN (cobol/online/cst/MARGIN.cob) - EXPECTED (1000: net revenue, estimate cost, margin arithmetic), reached only from
/// WHATIF MARGINWI; LINE/REALISED/ORDER/JOB have no production route (D-CST-04, SME-775).
/// </summary>
internal sealed class MarginEngine(CostNormEngine costNorm)
{
    public async Task LinkAsync(MarginArea a, CancellationToken ct)
    {
        var ca = a.Ca;
        Initialise(a);
        if (Validate(a)) return;
        switch (ca.Function)
        {
            case "EXPECTED":
                NetRevenue(a);
                await EstimateCostAsync(a, ct);
                ComputeMargin(a);
                break;
            case "LINE" or "REALISED" or "ORDER" or "JOB":
                throw new NotSupportedException($"MARGIN {ca.Function} has no production route (decision D-CST-04).");
            default:
                ca.Reject("CST0708", Pic.Str(200, "Function ", Pic.T(ca.Function), " is not supported by MARGIN"));
                break;
        }
    }

    private static void Initialise(MarginArea a)
    {
        a.Ca.Reset("MARGIN", "CST-013");
        a.NetRevenue = 0;
        a.VatAmount = 0;
        a.MarginValue = 0;
        a.MarginPct = 0;
        a.MarkupPct = 0;
        a.LinesCounted = 0;
        a.LinesNoCost = 0;
        a.Warning = "";
        a.CostBasisUsed = "";
        a.MarginState = "OK";
        a.MarkupState = "OK";
        a.Completeness = "COMPLETE";
        a.CostBasis = Pic.Upper(a.CostBasis);
        a.ItemCode = Pic.Upper(a.ItemCode);
        if (Pic.Blank(a.Currency)) a.Currency = "GBP";
        if (a.EffDate == 0) a.EffDate = a.Ca.BusinessDate;
        a.ResultNature = a.Ca.Function is "REALISED" or "JOB" ? "REALISED" : "EXPECTED";
    }

    private static bool Validate(MarginArea a)
    {
        if (Pic.Blank(a.Ca.Company))
        {
            a.Ca.Reject("CST0701", "A company is required");
            return true;
        }
        if (a.RevenueInclVat == "Y" && a.VatRate == 0)
        {
            a.Ca.Reject("CST0707", "A VAT inclusive revenue needs a VAT rate");
            return true;
        }
        if (a.Ca.Function == "LINE" && a.Quantity <= 0)
        {
            a.Ca.Reject("CST0705", "Quantity must be greater than zero");
            return true;
        }
        return false;
    }

    /// <summary>5000-NET-REVENUE: VAT stripped, then the discount; converted at the FX rate outside GBP.</summary>
    private static void NetRevenue(MarginArea a)
    {
        var gross = a.GrossRevenue;
        if (gross == 0 && a.Quantity != 0) gross = ((Cd)a.Quantity * a.UnitPrice).Store(13, 2, rounded: true);
        decimal net;
        if (a.RevenueInclVat == "Y")
        {
            net = ((Cd)gross / ((Cd)1 + (Cd)a.VatRate / 100)).Store(13, 2, rounded: true);
            a.VatAmount = ((Cd)gross - net).Store(13, 2);
        }
        else
        {
            net = gross;
            a.VatAmount = ((Cd)net * a.VatRate / 100).Store(13, 2, rounded: true);
        }
        net = ((Cd)net - a.Discount).Store(13, 2);
        a.NetRevenue = net;
        if (!Pic.Eq(a.Currency, "GBP") && a.FxRate > 0) a.NetRevenue = ((Cd)a.NetRevenue / a.FxRate).Store(13, 2, rounded: true, previous: a.NetRevenue);
    }

    /// <summary>5100-ESTIMATE-COST: the cost passed in, else COSTNORM CONTEXT for the requested basis.</summary>
    private async Task EstimateCostAsync(MarginArea a, CancellationToken ct)
    {
        if (a.CostAmount != 0)
        {
            a.CostBasisUsed = a.CostBasis;
            return;
        }
        if (a.UnitCost != 0)
        {
            a.CostAmount = ((Cd)a.UnitCost * a.Quantity).Store(13, 2, rounded: true);
            a.CostBasisUsed = a.CostBasis;
            return;
        }
        if (Pic.Blank(a.ItemCode) || a.Quantity <= 0)
        {
            a.Completeness = "INCOMPLETE";
            return;
        }
        var cn = new CostNormArea
        {
            Ca = CaHeader.Initialized("CONTEXT", a.Ca.Company, a.Ca.Site, a.Ca.User, a.Ca.BusinessDate),
            ItemCode = a.ItemCode,
            Quantity = a.Quantity,
            EffDate = a.EffDate,
            Purpose = "ENQUIRY",
            Basis = Pic.Blank(a.CostBasis) ? "STANDARD" : a.CostBasis,
        };
        cn.Ca.Program = "COSTNORM";
        await costNorm.LinkAsync(cn, ct);
        if (cn.Ca.ReturnCode > 4)
        {
            a.Completeness = "INCOMPLETE";
            a.Ca.ReasonCode = "CST0704";
            a.Warning = Pic.X(cn.Ca.Message, 80);
            a.Ca.ReturnCode = 4;
            a.LinesNoCost++;
        }
        else
        {
            a.CostAmount = cn.ExtCostGbp;
            a.UnitCost = cn.UnitCostGbp;
            a.CostBasisUsed = cn.BasisUsed;
        }
        a.LinesCounted++;
    }

    /// <summary>6000-COMPUTE-MARGIN.</summary>
    private static void ComputeMargin(MarginArea a)
    {
        a.CostAmount = ((Cd)a.CostAmount + a.Adjustments).Store(13, 2);
        a.MarginValue = ((Cd)a.NetRevenue - a.CostAmount).Store(13, 2);
        if (a.NetRevenue == 0)
        {
            a.MarginState = "UNDEFINED";
            a.MarginPct = 0;
            if (a.Ca.ReturnCode == 0)
            {
                a.Ca.ReasonCode = "CST0702";
                // STRING FUNCTION TRIM(CA-MESSAGE) "undefined": the trailing space of the first literal is trimmed away
                a.Ca.Message = "Net revenue is zero - margin percentage isundefined";
                a.Ca.ReturnCode = 4;
            }
        }
        else
        {
            a.MarginPct = ((Cd)a.MarginValue * 100 / a.NetRevenue).Store(7, 4, rounded: true, previous: a.MarginPct);
        }
        if (a.CostAmount == 0)
        {
            a.MarkupState = "UNDEFINED";
            a.MarkupPct = 0;
        }
        else
        {
            a.MarkupPct = ((Cd)a.MarginValue * 100 / a.CostAmount).Store(7, 4, rounded: true, previous: a.MarkupPct);
        }
    }
}

/// <summary>
/// COSTNORM (cobol/online/cst/COSTNORM.cob) - CONTEXT (1000-1400), reached only from MARGIN EXPECTED. NORMCOST, PURCOST and
/// ELEMENTS have no production route (D-CST-04), so IC-ITM-UOMCONV is never invoked.
/// </summary>
internal sealed class CostNormEngine(ISqlRunner sql)
{
    public async Task LinkAsync(CostNormArea a, CancellationToken ct)
    {
        var ca = a.Ca;
        var db = new CstSqlio(sql, ca);
        Initialise(a);
        if (Validate(a)) return;
        switch (ca.Function)
        {
            case "CONTEXT":
                await ContextAsync(a, db, ct);
                break;
            case "NORMCOST" or "PURCOST" or "ELEMENTS":
                throw new NotSupportedException($"COSTNORM {ca.Function} has no production route (decision D-CST-04).");
            default:
                ca.Reject("CST0109", Pic.Str(200, "Function ", Pic.T(ca.Function), " is not supported by COSTNORM"));
                break;
        }
    }

    private static void Initialise(CostNormArea a)
    {
        a.Ca.Reset("COSTNORM", "CST-003");
        a.UnitCostGbp = 0;
        a.ExtCostGbp = 0;
        a.ElMaterial = 0;
        a.Warning = "";
        a.Source = "";
        a.BasisUsed = "";
        a.ApprovalState = "";
        a.Evidence = "";
        a.Provisional = "N";
        a.Completeness = "COMPLETE";
        a.ItemCode = Pic.Upper(a.ItemCode);
        a.Purpose = Pic.Upper(a.Purpose);
        a.Basis = Pic.Upper(a.Basis);
        a.Currency = Pic.Upper(a.Currency);
        if (Pic.Blank(a.Currency)) a.Currency = "GBP";
        if (Pic.Blank(a.Purpose)) a.Purpose = "ENQUIRY";
        if (Pic.Eq(a.Purpose, "QUOTE"))
        {
            a.Provisional = "Y";
            a.Evidence = "Quote estimate - not an inventory valuation";
        }
        if (a.EffDate == 0) a.EffDate = a.Ca.BusinessDate;
        if (a.PricePerQty == 0) a.PricePerQty = 1;
    }

    private static bool Validate(CostNormArea a)
    {
        if (Pic.Blank(a.Ca.Company) || Pic.Blank(a.ItemCode))
        {
            a.Ca.Reject("CST0101", "A company and an item code are required");
            return true;
        }
        if (a.VatRecoverPct < 0 || a.VatRecoverPct > 100)
        {
            a.Ca.Reject("CST0107", "VAT recovery percentage must be 0 to 100");
            return true;
        }
        return false;
    }

    /// <summary>1000-CONTEXT: the asked-for basis or "not available" - never another basis.</summary>
    private static async Task ContextAsync(CostNormArea a, CstSqlio db, CancellationToken ct)
    {
        var ca = a.Ca;
        if (Pic.Blank(a.Basis)) a.Basis = "AVERAGE";
        var found = false;
        switch (Pic.X(a.Basis, 12))
        {
            case "AVERAGE":
                await db.SingleAsync("SELECT AverageUnitCost, CostMethod FROM cst.ItemCost WHERE CompanyCode = ? AND ItemCode = ? AND " +
                                     "Revision = ? AND SiteCode = '*'", 2,
                    [SqlioParm.Text(ca.Company), SqlioParm.Text(a.ItemCode), SqlioParm.Text(a.Revision)], ct);
                if (db.RowFound)
                {
                    a.UnitCostGbp = Pic.N(db.Row.NumVal(0), 11, 6);
                    found = true;
                    a.Source = "cst.ItemCost";
                    a.ApprovalState = Pic.X(db.Row.Raw(1), 12);
                }
                break;
            case "STANDARD":
                await db.SingleAsync("SELECT sc.TotalUnitCost, cs.CostSetCode FROM cst.StandardCost sc JOIN cst.CostSet cs ON " +
                                     "cs.CompanyCode = sc.CompanyCode AND cs.CostSetCode = sc.CostSetCode WHERE sc.CompanyCode = ? AND " +
                                     "sc.ItemCode = ? AND cs.Status = 'ACTIVE' AND cs.Purpose = 'STANDARD' AND cs.EffectiveFrom <= ? " +
                                     "ORDER BY cs.EffectiveFrom DESC", 2,
                    [SqlioParm.Text(ca.Company), SqlioParm.Text(a.ItemCode), SqlioParm.Digits(a.EffDate, 8)], ct);
                if (db.RowFound)
                {
                    a.UnitCostGbp = Pic.N(db.Row.NumVal(0), 11, 6);
                    found = true;
                    a.Source = "cst.StandardCost";
                    a.CostSet = Pic.X(db.Row.Raw(1), 20);
                    a.ApprovalState = "ACTIVE";
                }
                break;
            case "LASTPURCH":
                await db.SingleAsync("SELECT LastPurchaseCost FROM cst.ItemCost WHERE CompanyCode = ? AND ItemCode = ? AND " +
                                     "LastPurchaseCost IS NOT NULL", 1, [SqlioParm.Text(ca.Company), SqlioParm.Text(a.ItemCode)], ct);
                if (db.RowFound)
                {
                    a.UnitCostGbp = Pic.N(db.Row.NumVal(0), 11, 6);
                    found = true;
                    a.Source = "cst.ItemCost";
                    a.ApprovalState = "REFERENCE";
                    a.Warning = "A last purchase price is a commercial reference";
                    ca.ReturnCode = 4;
                }
                break;
            case "FIFO":
                await db.SingleAsync("SELECT SUM(QtyRemaining), SUM(ValueRemainingGBP + LandedAdjustmentGBP) FROM cst.CostLayer WHERE " +
                                     "CompanyCode = ? AND ItemCode = ? AND IsClosed = 0", 2,
                    [SqlioParm.Text(ca.Company), SqlioParm.Text(a.ItemCode)], ct);
                if (db.RowFound)
                {
                    var qty = Pic.N(db.Row.NumVal(0), 13, 6);
                    var amt = Pic.N(db.Row.NumVal(1), 13, 6);
                    if (qty > 0)
                    {
                        a.UnitCostGbp = ((Cd)amt / qty).Store(11, 6, rounded: true, previous: a.UnitCostGbp);
                        found = true;
                        a.Source = "cst.CostLayer";
                        a.ApprovalState = "LAYERED";
                    }
                }
                break;
            default:
                ca.Reject("CST0102", Pic.Str(200, "Cost basis ", Pic.T(a.Basis), " is not one of AVERAGE, STANDARD, LASTPURCH or FIFO"));
                return;
        }
        if (db.SqlFailed) return;
        if (!found)
        {
            a.Completeness = "INCOMPLETE";
            a.UnitCostGbp = 0;
            a.BasisUsed = "";
            ca.ReasonCode = "CST0102";
            ca.Message = Pic.Str(200, "No ", Pic.T(a.Basis), " cost is held for item ", Pic.T(a.ItemCode), " - another basis will not be substituted");
            ca.ReturnCode = 16;
            return;
        }
        a.BasisUsed = Pic.X(a.Basis, 12);
        a.Currency = "GBP";
        a.ExtCostGbp = ((Cd)a.UnitCostGbp * a.Quantity).Store(13, 2, rounded: true);
        a.ElMaterial = a.ExtCostGbp;
    }
}
