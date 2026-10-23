using Sterp.Modules.Kit.Common;
using Sterp.Modules.Kit.Data;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Kit.Application;

/// <summary>KITCOSTCA (cobol/copy/KITCOSTCA.cpy).</summary>
internal sealed class KitCostArea : KitArea
{
    public string KitRef { get; set; } = "";
    public string CostSet { get; set; } = "";
    public int AsAt { get; set; }
    public string Currency { get; set; } = "";
    public decimal PlannedGbp { get; set; }
    public decimal IssuedGbp { get; set; }
    public decimal ReturnedGbp { get; set; }
    public decimal ScrappedGbp { get; set; }
    public decimal AbsorbedGbp { get; set; }
    public decimal CompletedGbp { get; set; }
    public decimal ResidualGbp { get; set; }
    public decimal VarianceGbp { get; set; }
    public decimal PlannedQty { get; set; }
    public decimal CompletedQty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal PlanUnitCost { get; set; }
    public int SubassyCnt { get; set; }
    public decimal SubassyGbp { get; set; }
    public string DoubleCheck { get; set; } = "";
    public string Reconciled { get; set; } = "";
    public string KitStatus { get; set; } = "";
    public string NoteText { get; set; } = "";
    public int ElemCnt { get; set; }
    public KitCostElement[] Elems { get; } = Enumerable.Range(0, 12).Select(_ => new KitCostElement()).ToArray();
}

/// <summary>One KK-ELEMS occurrence.</summary>
internal sealed class KitCostElement
{
    public string Element { get; set; } = "";
    public string Desc { get; set; } = "";
    public decimal Planned { get; set; }
    public decimal Actual { get; set; }
    public decimal Variance { get; set; }
    public decimal Qty { get; set; }
    public string Uom { get; set; } = "";

    public KitCostElement Copy() => (KitCostElement)MemberwiseClone();
}

/// <summary>
/// KITCOST (cobol/online/kit/KITCOST.cob:90-579): the seven values of a kit and the residual identity. COST, GETELEM and
/// VARIANCE all build the same picture. Read only; no state is carried between calls (every call starts with Clear-Answer).
/// </summary>
internal sealed class KitCost(KitDb kitDb, ISqlRunner sql)
{
    private const string ActualSelect =
        "SELECT i.IssueType, CONVERT(varchar(20),SUM(i.ValueGBP))," +
        " CONVERT(varchar(26),SUM(i.Quantity)), MAX(ISNULL(i.UomCode,''))" +
        " FROM kit.KitIssue i" +
        " WHERE i.CompanyCode = ? AND i.KitRef = ?" +
        " AND (? = 0 OR i.PostingDate <= CONVERT(date,?,112))" +
        " GROUP BY i.IssueType";

    private const string SubassySelect =
        "SELECT CONVERT(varchar(12),COUNT(DISTINCT c.ExecutionKitId))," +
        " CONVERT(varchar(20),ISNULL(SUM(pi.ValueGBP),0))," +
        " CONVERT(varchar(12),(" +
        "   SELECT COUNT(*) FROM kit.KitIssue d" +
        "   INNER JOIN kit.ExecutionKit ck" +
        "     ON ck.CompanyCode = d.CompanyCode" +
        "    AND ck.ParentKitRef = ?" +
        "   INNER JOIN kit.ExecutionKitLine cl" +
        "     ON cl.ExecutionKitId = ck.ExecutionKitId" +
        "    AND cl.ComponentItemCode = d.ComponentItemCode" +
        "   WHERE d.CompanyCode = ? AND d.KitRef = ?))" +
        " FROM kit.ExecutionKit c" +
        " LEFT JOIN kit.KitIssue pi" +
        "   ON pi.CompanyCode = c.CompanyCode" +
        "  AND pi.KitRef = ?" +
        "  AND pi.ComponentItemCode = c.ParentItemCode" +
        " WHERE c.CompanyCode = ? AND c.ParentKitRef = ?";

    private static readonly int[] Widths100 = [100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100];

    private readonly KitDbCtl _kd = new();
    private readonly KitHeader _kh = new();
    private readonly KitLine _kl = new();
    private readonly KitItem _ki = new();

    // Ws-Totals (S9(13)V99 / S9(13)V9(6)) and the twelve elements
    private decimal _planned, _issued, _returned, _scrapped, _absorbed, _completed, _residual, _subassy, _planSubassy;
    private decimal _planQty, _actQty;
    private int _subassyCnt;
    private long _dupCnt;
    private int _lineCnt;
    private readonly Element[] _el = Enumerable.Range(0, 12).Select(_ => new Element()).ToArray();

    private sealed class Element
    {
        public string Code = "";
        public string Desc = "";
        public decimal Planned;
        public decimal Actual;
        public decimal Qty;
        public string Uom = "";
        public string Used = "N";
    }

    public async Task LinkAsync(KitCostArea ca, CancellationToken ct)
    {
        ca.Program = "KITCOST";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "KIT-020";
        ClearAnswer(ca);
        switch (ca.Function)
        {
            case "COST":
            case "GETELEM":
            case "VARIANCE":
                await BuildCostPictureAsync(ca, ct);
                break;
            default:
                ca.Set(8, "KIT0002", Pic.Msg("KITCOST does not support the function ", Pic.Trim(ca.Function)));
                break;
        }
    }

    private void ClearAnswer(KitCostArea ca)
    {
        _planned = _issued = _returned = _scrapped = _absorbed = _completed = _residual = _subassy = _planSubassy = 0m;
        _planQty = _actQty = 0m;
        ca.PlannedGbp = ca.IssuedGbp = ca.ReturnedGbp = ca.ScrappedGbp = ca.AbsorbedGbp = 0m;
        ca.CompletedGbp = ca.ResidualGbp = ca.VarianceGbp = ca.UnitCost = ca.PlanUnitCost = 0m;
        ca.SubassyCnt = 0;
        ca.SubassyGbp = 0m;
        ca.ElemCnt = 0;
        ca.DoubleCheck = "N";
        ca.Reconciled = "N";
        ca.NoteText = "";
        _subassyCnt = 0;
        _dupCnt = 0;
        _lineCnt = 0;
        if (Pic.Blank(ca.Currency)) ca.Currency = "GBP";
        if (Pic.Blank(ca.CostSet)) ca.CostSet = "STD2026";
        ClearElements(ca);
    }

    private void ClearElements(KitCostArea ca)
    {
        foreach (var e in _el)
        {
            e.Code = e.Desc = e.Uom = "";
            e.Planned = e.Actual = e.Qty = 0m;
            e.Used = "N";
        }
        (string Code, string Desc)[] fixedRows =
        [
            ("MATERIAL", "Bought out material issued"), ("SUBASSY", "Made in subassemblies"), ("RETURN", "Material returned to store"),
            ("SCRAP", "Material scrapped on the job"), ("LABOUR", "Conversion labour absorbed"), ("OVERHEAD", "Overhead absorbed"),
            ("SUBCON", "Subcontract services"), ("COMPLETE", "Value transferred to stock"),
        ];
        for (var i = 0; i < fixedRows.Length; i++)
        {
            _el[i].Code = fixedRows[i].Code;
            _el[i].Desc = fixedRows[i].Desc;
        }
        ca.ElemCnt = 8;
    }

    private static decimal T(decimal v) => Pic.N(v, 13, 2);

    // ------------------------------------------------------------------ Build-Cost-Picture

    private async Task BuildCostPictureAsync(KitCostArea ca, CancellationToken ct)
    {
        if (Pic.Blank(ca.KitRef))
        {
            ca.Set(8, "KIT0001", "A kit reference is required.");
            return;
        }
        await LoadKitAsync(ca, ct);
        if (ca.Failed) return;
        await AccumulatePlannedAsync(ca, ct);
        if (ca.Failed) return;
        await AccumulateActualAsync(ca, ct);
        if (ca.Failed) return;
        await CheckSubassemblyOnceAsync(ca, ct);
        if (ca.ReturnCode == 12) return;
        DeriveCompletedIfMissing();
        ComputeResidual(ca);
        PublishTotals(ca);
        PublishElements(ca);
        WriteNote(ca);
    }

    private async Task LoadKitAsync(KitCostArea ca, CancellationToken ct)
    {
        _kd.Initialize();
        _kh.Initialize();
        _kd.Func = "HDRGET";
        _kd.Company = ca.Company;
        _kd.User = ca.User;
        _kd.KitRef = Pic.X(ca.KitRef, 30);
        await kitDb.CallAsync(_kd, _kh, _kl, _ki, ct);
        switch (_kd.Rc)
        {
            case 0:
                ca.KitStatus = _kh.Status;
                ca.PlannedQty = _kh.PlannedQty;
                ca.CompletedQty = _kh.CompletedQty;
                _planQty = _kh.PlannedQty;
                _actQty = _kh.CompletedQty;
                break;
            case 100:
                ca.Set(16, "KIT0001", Pic.Msg("Kit ", Pic.Trim(ca.KitRef), " is not known in this company."));
                break;
            default:
                ca.Set(12, "SQL0001", _kd.ErrMsg);
                break;
        }
    }

    private async Task AccumulatePlannedAsync(KitCostArea ca, CancellationToken ct)
    {
        _kd.Func = "LINOPEN";
        _kd.KitId = _kh.ExecKitId;
        _kd.OnlyOpen = "N";
        await kitDb.CallAsync(_kd, _kh, _kl, _ki, ct);
        if (_kd.Rc != 0)
        {
            ca.Set(12, "SQL0001", _kd.ErrMsg);
            return;
        }
        var more = true;
        while (more)
        {
            _kd.Func = "LINNEXT";
            _kl.Initialize();
            await kitDb.CallAsync(_kd, _kh, _kl, _ki, ct);
            switch (_kd.Rc)
            {
                case 0:
                    AddPlannedLine();
                    break;
                case 100:
                    more = false;
                    break;
                default:
                    ca.Set(12, "SQL0001", _kd.ErrMsg);
                    more = false;
                    break;
            }
        }
        _kd.Func = "LINCLOS";
        await kitDb.CallAsync(_kd, _kh, _kl, _ki, ct);
    }

    private void AddPlannedLine()
    {
        if (_kl.Status == "CANCELLED") return;
        _lineCnt++;
        var value = CobolNumeric.Multiply(_kl.CurrentReq, _kl.PlanUnitCost, 13, 2, rounded: true);
        _planned = T(_planned + value);
        _el[0].Planned = T(_el[0].Planned + value);
        _el[0].Qty = Pic.Q(_el[0].Qty + _kl.CurrentReq);
        if (Pic.Blank(_el[0].Uom)) _el[0].Uom = _kl.UomCode;
        _el[0].Used = "Y";
    }

    private async Task AccumulateActualAsync(KitCostArea ca, CancellationToken ct)
    {
        var asAt = SqlioParm.Integer(ca.AsAt);
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await sql.FetchAllAsync(ActualSelect,
                [SqlioParm.Text(Pic.Pad(ca.Company, 10)), SqlioParm.Text(Pic.Pad(ca.KitRef, 30)), asAt,
                 ca.AsAt == 0 ? SqlioParm.Text("99991231") : asAt],
                Widths100, ct);
        }
        catch (SqlioException ex)
        {
            ca.Set(12, "SQL0001", Pic.X(ex.DriverText, 200));
            return;
        }
        foreach (var row in rows) ApplyMovementGroup(row);
    }

    private void ApplyMovementGroup(SqlioRow row)
    {
        var type = Pic.X(row.Raw(0), 12);
        var value = Pic.R(row.NumVal(1), 13, 2);
        switch (type)
        {
            case "ISSUE":
            case "SUBSTISSUE":
            case "BACKFLUSH":
                _issued = T(_issued + value);
                _el[0].Actual = T(_el[0].Actual + value);
                _el[0].Used = "Y";
                break;
            case "RETURN":
                _returned = T(_returned + value);
                _el[2].Actual = T(_el[2].Actual + value);
                _el[2].Used = "Y";
                break;
            case "SCRAP":
                _scrapped = T(_scrapped + value);
                _el[3].Actual = T(_el[3].Actual + value);
                _el[3].Used = "Y";
                break;
            case "LABOUR":
                _absorbed = T(_absorbed + value);
                _el[4].Actual = T(_el[4].Actual + value);
                _el[4].Used = "Y";
                break;
            case "OVERHEAD":
                _absorbed = T(_absorbed + value);
                _el[5].Actual = T(_el[5].Actual + value);
                _el[5].Used = "Y";
                break;
            case "SUBCON":
                _absorbed = T(_absorbed + value);
                _el[6].Actual = T(_el[6].Actual + value);
                _el[6].Used = "Y";
                break;
            case "COMPLETE":
                _completed = T(_completed + value);
                _el[7].Actual = T(_el[7].Actual + value);
                _el[7].Used = "Y";
                break;
        }
    }

    private async Task CheckSubassemblyOnceAsync(KitCostArea ca, CancellationToken ct)
    {
        var kit = SqlioParm.Text(Pic.Pad(ca.KitRef, 30));
        var co = SqlioParm.Text(Pic.Pad(ca.Company, 10));
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(SubassySelect, [kit, co, kit, kit, co, kit], Widths100, ct);
        }
        catch (SqlioException ex)
        {
            ca.Set(12, "SQL0001", Pic.X(ex.DriverText, 200));
            return;
        }
        if (!row.Found)
        {
            ca.DoubleCheck = "Y";
            return;
        }
        _subassyCnt = (int)Pic.N(row.NumVal(0), 4, 0);
        _subassy = Pic.R(row.NumVal(1), 13, 2);
        _dupCnt = (long)decimal.Truncate(row.NumVal(2));
        ca.SubassyCnt = _subassyCnt;
        ca.SubassyGbp = Pic.M(_subassy);
        _el[1].Actual = T(_el[1].Actual + _subassy);
        if (_subassyCnt > 0) _el[1].Used = "Y";
        if (_dupCnt == 0)
        {
            ca.DoubleCheck = "Y";
        }
        else
        {
            ca.DoubleCheck = "N";
            ca.Set(4, "KIT0027", "A child kit component has also been charged to this parent kit - the subassembly is counted twice.");
        }
    }

    private void DeriveCompletedIfMissing()
    {
        if (_completed != 0) return;
        if (_actQty <= 0 || _planQty <= 0) return;
        var share = CobolNumeric.Divide(_actQty, _planQty, 9, 9);
        if (share > 1) share = 1m;
        _completed = CobolNumeric.Multiply(_issued - _returned + _absorbed, share, 13, 2, rounded: true);
        _el[7].Actual = T(_el[7].Actual + _completed);
        _el[7].Used = "Y";
        // Ws-Note "Completed value derived pro rata from the output quantity." is overwritten by Write-Note before any use
    }

    private void ComputeResidual(KitCostArea ca)
    {
        _residual = Pic.R(_issued - _returned + _absorbed - _completed, 13, 2);
        var check = T(_issued - _returned + _absorbed - _completed - _residual);
        ca.Reconciled = Math.Abs(check) <= 0.01m ? "Y" : "N";
    }

    private void PublishTotals(KitCostArea ca)
    {
        ca.PlannedGbp = Pic.M(_planned);
        ca.IssuedGbp = Pic.M(_issued);
        ca.ReturnedGbp = Pic.M(_returned);
        ca.ScrappedGbp = Pic.M(_scrapped);
        ca.AbsorbedGbp = Pic.M(_absorbed);
        ca.CompletedGbp = Pic.M(_completed);
        ca.ResidualGbp = Pic.M(_residual);
        ca.VarianceGbp = Pic.R(_issued - _returned + _absorbed - _planned, 11, 2);
        if (_planQty > 0) ca.PlanUnitCost = CobolNumeric.Divide(_planned, _planQty, 11, 6, rounded: true);
        if (_actQty > 0) ca.UnitCost = CobolNumeric.Divide(_completed, _actQty, 11, 6, rounded: true);
    }

    private void PublishElements(KitCostArea ca)
    {
        _el[1].Planned = Pic.R(_planSubassy, 13, 2);
        _el[7].Planned = _planned;
        ca.ElemCnt = 0;
        foreach (var e in _el)
        {
            if (e.Used != "Y" && e.Planned == 0) continue;
            var k = ca.Elems[ca.ElemCnt++];
            k.Element = Pic.X(e.Code, 10);
            k.Desc = Pic.X(e.Desc, 30);
            k.Planned = Pic.M(e.Planned);
            k.Actual = Pic.M(e.Actual);
            k.Variance = Pic.M(e.Actual - e.Planned);
            k.Qty = Pic.Q(e.Qty);
            k.Uom = Pic.X(e.Uom, 10);
        }
    }

    private void WriteNote(KitCostArea ca)
    {
        var money = CobolEdit.Format(_residual, "-(11)9.99");
        var cnt = CobolEdit.Format(_subassyCnt, "Z(3)9");
        ca.NoteText = Pic.Str(200, "Residual WIP ", Pic.Trim(ca.Currency), " ", money,
            " = issued - returned + absorbed - completed;", " scrap is inside issued. Subassemblies ", cnt);
        var note = Pic.Pad(ca.NoteText, 200);
        ca.NoteText = ca.DoubleCheck == "Y"
            ? Pic.Str(200, Pic.Trim(note), ", each charged once.")
            : Pic.Str(200, Pic.Trim(note), ", DOUBLE COUNTED - see the child kits.");
    }
}

/// <summary>KitExecutionService.Cost (KitExecutionService.vb:136-149).</summary>
internal sealed class KitCostService(KitCost kitCost)
{
    public async Task<(LegacyResult Result, KitCostArea Area, List<KitCostElement> Elements)> CostAsync(KitSession s, string kitRef, string costSet,
        bool varianceOnly, CancellationToken ct)
    {
        var ca = new KitCostArea
        {
            KitRef = Pic.X(kitRef, 30),
            CostSet = Pic.X(costSet, 20),
            AsAt = s.BusinessDate,
            Currency = "GBP",
            DoubleCheck = "Y",
        };
        var elements = new List<KitCostElement>();
        ca.Prepare(s, varianceOnly ? "VARIANCE" : "COST");
        await kitCost.LinkAsync(ca, ct);
        var r = ca.Result;
        if (r.Succeeded)
        {
            ca.Prepare(s, "GETELEM");
            await kitCost.LinkAsync(ca, ct);
            r = ca.Result;
        }
        if (r.Succeeded)
        {
            for (var i = 0; i < ca.ElemCnt && i < ca.Elems.Length; i++) elements.Add(ca.Elems[i].Copy());
        }
        return (r, ca, elements);
    }
}
