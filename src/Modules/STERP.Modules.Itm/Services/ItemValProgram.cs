using Sterp.Modules.Itm.Data;

namespace Sterp.Modules.Itm.Services;

/// <summary>ITEMVALCA body (cobol/copy/ITEMVALCA.cpy, prefix IV-) - internal to ITM.</summary>
internal sealed class ItemvalArea
{
    public CaHeader Ca { get; set; } = new();
    public string ItemCode { get; set; } = "";
    public string SearchText { get; set; } = "";
    public string PartnerType { get; set; } = "";
    public string PartnerCode { get; set; } = "";
    public string Action { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Description { get; set; } = "";
    public string ItemClass { get; set; } = "";
    public string Category { get; set; } = "";
    public string Steward { get; set; } = "";
    public string Lifecycle { get; set; } = "";
    public string StockUom { get; set; } = "";
    public string Traceability { get; set; } = "";
    public int ShelfLifeDays { get; set; }
    public decimal Quantum { get; set; }
    public string Indivisible { get; set; } = "";
    public string MakeBuy { get; set; } = "";
    public string ValuationGrp { get; set; } = "";
    public string CostMethod { get; set; } = "";
    public string CurrentRev { get; set; } = "";
    public string IsStocked { get; set; } = "";
    public string IsPurchasable { get; set; } = "";
    public string IsSaleable { get; set; } = "";
    public string IsManufactured { get; set; } = "";
    public string IsConsumable { get; set; } = "";
    public string ReqInspection { get; set; } = "";
    public string DrcGoods { get; set; } = "";
    public string Successor { get; set; } = "";
    public string Predecessor { get; set; } = "";
    public string RevStatus { get; set; } = "";
    public int RevFrom { get; set; }
    public int RevTo { get; set; }
    public string RevInterchange { get; set; } = "";
    public string Allowed { get; set; } = "";
    public string Ready { get; set; } = "";
    public List<ItemvalFault> Faults { get; } = [];
}

/// <summary>One IV-FAULTS entry (IV-F-REQ X(8), IV-F-SEVERITY X(8), IV-F-FIELD X(24), IV-F-TEXT X(90)).</summary>
internal sealed record ItemvalFault(string Req, string Severity, string Field, string Text);

/// <summary>
/// ITEMVAL (cobol/online/itm/ITEMVAL.cob) functions CLASSCHK (item class gate, STKMOVE) and VALIDATE (activation review,
/// FR-STERP-ITM-011). Reads only, autocommit. SEARCH, FETCH, RESOLVE, ACTIVATE and REVISION are deferred (D-ITM-04).
/// </summary>
internal sealed class ItemValProgram(Sqlio sql)
{
    private const string Prog = "ITEMVAL";

    private bool _error;
    private bool _itemRead;
    private string _invAcctGrp = "";
    private string _revAcctGrp = "";
    private int _minShelf;
    private string _ukca = "N";
    private string _conformRef = "";
    private string _dgUn = "";
    private string _dgClass = "";
    private string _dgPack = "";
    private string _weee = "";
    private string _battery = "";
    private string _severity = "";
    private string _reqId = "";

    public async Task RunAsync(ItemvalArea iv, CancellationToken ct)
    {
        var ca = iv.Ca;
        Initialise(iv);
        Validate(iv);
        if (_error) return;
        switch (ca.Function)
        {
            case "CLASSCHK":
                await ClassCheckAsync(iv, ct);
                break;
            case "VALIDATE":
                await ActivationReviewAsync(iv, ct);
                break;
            case "FETCH" or "SEARCH" or "RESOLVE" or "ACTIVATE" or "REVISION":
                throw new NotSupportedException($"ITEMVAL {ca.Function} is a test-harness-only function deferred by D-ITM-04.");
            default:
                ca.ReasonCode = "ITM0008";
                ca.SetMessage("ITEMVAL does not support function " + Cob.Trim(ca.Function));
                Reject(ca);
                break;
        }
    }

    private void Initialise(ItemvalArea iv)
    {
        var ca = iv.Ca;
        _error = false;
        _itemRead = false;
        ca.Program = Prog;
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "ITM-003";
        iv.Faults.Clear();
        iv.Allowed = "N";
        iv.Ready = "N";
        iv.ItemCode = Cob.Upper(iv.ItemCode, 40);
        iv.Action = Cob.Upper(iv.Action, 8);
        iv.PartnerType = Cob.Upper(iv.PartnerType, 10);
        if (ca.BusinessDate == 0) ca.BusinessDate = 20261005;
    }

    private void Validate(ItemvalArea iv)
    {
        var ca = iv.Ca;
        if (Cob.Spaces(ca.Company))
        {
            ca.ReasonCode = "ITM0001";
            ca.SetMessage("A company must be supplied");
            Reject(ca);
            return;
        }
        if (ca.Function is "SEARCH" or "RESOLVE")
        {
            if (Cob.Spaces(iv.SearchText))
            {
                ca.ReasonCode = "ITM0009";
                ca.SetMessage("Enter a code, a description or a partner ");
                Reject(ca);
            }
            return;
        }
        if (Cob.Spaces(iv.ItemCode))
        {
            ca.ReasonCode = "ITM0001";
            ca.SetMessage("An item code is required");
            Reject(ca);
        }
    }

    /// <summary>0300-READ-ITEM.</summary>
    private async Task ReadItemAsync(ItemvalArea iv, CancellationToken ct)
    {
        if (_itemRead) return;
        var ca = iv.Ca;
        var r = await sql.SingleAsync(
            "SELECT Description, ItemClass, ISNULL(Category,''), ISNULL(StewardUser,''), LifecycleStatus, " +
            "StockUomCode, TraceabilityType, ISNULL(ShelfLifeDays,0), QuantumQty, IsIndivisible, MakeBuy, ValuationGroup, " +
            "CostMethod, CurrentRevision, IsStocked, IsPurchasable, IsSaleable, IsManufacturable, IsConsumable, RequiresInspection, " +
            "IsDrcSpecifiedGoods, ISNULL(SuccessorItemCode,''), ISNULL(PredecessorItemCode,''), ISNULL(CreatedBy,'') " +
            "FROM inv.Item WHERE CompanyCode = @p1 AND ItemCode = @p2",
            [ca.Company, iv.ItemCode], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound)
        {
            ca.ReasonCode = "ITM0001";
            ca.SetMessage("Item " + Cob.Trim(iv.ItemCode) + " is not held in company " + Cob.Trim(ca.Company));
            ca.ReturnCode = 16;
            _error = true;
            ca.AddMessage();
            return;
        }
        var row = r.Row;
        iv.Description = Cob.X(Col.Text(row, 0), 100);
        iv.ItemClass = Cob.X(Col.Text(row, 1), 12);
        iv.Category = Cob.X(Col.Text(row, 2), 20);
        iv.Steward = Cob.X(Col.Text(row, 3), 20);
        iv.Lifecycle = Cob.X(Col.Text(row, 4), 10);
        iv.StockUom = Cob.X(Col.Text(row, 5), 10);
        iv.Traceability = Cob.X(Col.Text(row, 6), 8);
        iv.ShelfLifeDays = (int)Sterp.Platform.Kernel.Cobol.CobolNumeric.Store(Col.Num(row, 7), 5, 0);
        iv.Quantum = Cob.Q(Col.Num(row, 8));
        iv.Indivisible = YesNo(row, 9);
        iv.MakeBuy = Cob.X(Col.Text(row, 10), 6);
        iv.ValuationGrp = Cob.X(Col.Text(row, 11), 20);
        iv.CostMethod = Cob.X(Col.Text(row, 12), 4);
        iv.CurrentRev = Cob.X(Col.Text(row, 13), 4);
        iv.IsStocked = YesNo(row, 14);
        iv.IsPurchasable = YesNo(row, 15);
        iv.IsSaleable = YesNo(row, 16);
        iv.IsManufactured = YesNo(row, 17);
        iv.IsConsumable = YesNo(row, 18);
        iv.ReqInspection = YesNo(row, 19);
        iv.DrcGoods = YesNo(row, 20);
        iv.Successor = Cob.X(Col.Text(row, 21), 40);
        iv.Predecessor = Cob.X(Col.Text(row, 22), 40);
        _itemRead = true;
    }

    private static string YesNo(object?[] row, int i) => Col.Bit(row, i) ? "Y" : "N";

    /// <summary>0350-READ-COMPLIANCE.</summary>
    private async Task ReadComplianceAsync(ItemvalArea iv, CancellationToken ct)
    {
        var ca = iv.Ca;
        var r = await sql.SingleAsync(
            "SELECT ISNULL(InventoryAccountGroup,''), ISNULL(RevenueAccountGroup,''), ISNULL(MinRemainingShelfLifeDays,0), UkcaMarked, " +
            "ISNULL(ConformityDocRef,''), ISNULL(DangerousGoodsUn,''), ISNULL(DangerousGoodsClass,''), ISNULL(DangerousGoodsPackGroup,''), " +
            "ISNULL(WeeeCategory,''), ISNULL(BatteryCategory,''), ISNULL(NetWeightKg,0) FROM inv.Item WHERE CompanyCode = @p1 AND ItemCode = @p2",
            [ca.Company, iv.ItemCode], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound) return;
        var row = r.Row;
        _invAcctGrp = Cob.X(Col.Text(row, 0), 20);
        _revAcctGrp = Cob.X(Col.Text(row, 1), 20);
        _minShelf = (int)Sterp.Platform.Kernel.Cobol.CobolNumeric.Store(Col.Num(row, 2), 5, 0);
        _ukca = YesNo(row, 3);
        _conformRef = Cob.X(Col.Text(row, 4), 40);
        _dgUn = Cob.X(Col.Text(row, 5), 8);
        _dgClass = Cob.X(Col.Text(row, 6), 8);
        _dgPack = Cob.X(Col.Text(row, 7), 4);
        _weee = Cob.X(Col.Text(row, 8), 20);
        _battery = Cob.X(Col.Text(row, 9), 20);
    }

    // ------------------------------------------------------------------ 3000 CLASSCHK

    private async Task ClassCheckAsync(ItemvalArea iv, CancellationToken ct)
    {
        var ca = iv.Ca;
        await ReadItemAsync(iv, ct);
        if (ca.Failed) return;
        iv.Allowed = "N";
        switch (iv.Action)
        {
            case "STOCK": MayStock(iv); break;
            case "SELL":
                iv.Allowed = iv.ItemClass is "COSTONLY" or "PHANTOM" or "PACKAGING" ? "N" : iv.IsSaleable;
                break;
            case "BUY":
                iv.Allowed = iv.ItemClass is "COSTONLY" or "PHANTOM" or "KIT" ? "N" : iv.IsPurchasable;
                if (iv.Allowed == "Y" && iv.MakeBuy == "MAKE")
                {
                    iv.Allowed = "N";
                    ca.SetMessage("Item " + Cob.Trim(iv.ItemCode) + " is a make item; change the supply authority to BUY or DUAL before purchasing it");
                }
                break;
            case "MAKE":
                iv.Allowed = iv.ItemClass is "MADE" or "KIT" ? iv.IsManufactured : "N";
                break;
            case "BOM":
                iv.Allowed = iv.ItemClass == "SERVICE" ? "N" : "Y";
                break;
            case "KIT":
                iv.Allowed = iv.ItemClass is "SERVICE" or "PHANTOM" or "COSTONLY" ? "N" : "Y";
                break;
            case "COST":
                iv.Allowed = "Y";
                break;
            default:
                ca.ReasonCode = "ITM0008";
                ca.SetMessage("Action " + Cob.Trim(iv.Action) + " is not a classification rule");
                Reject(ca);
                return;
        }
        if (iv.Allowed == "Y")
        {
            ca.SetMessage("Class " + Cob.Trim(iv.ItemClass) + " permits " + Cob.Trim(iv.Action));
        }
        else
        {
            ca.ReasonCode = "ITM0003";
            if (Cob.Spaces(ca.Message))
            {
                ca.SetMessage("An item of class " + Cob.Trim(iv.ItemClass) + " may not take part in " + Cob.Trim(iv.Action));
            }
            Reject(ca);
        }
    }

    private static void MayStock(ItemvalArea iv)
    {
        var ca = iv.Ca;
        iv.Allowed = iv.ItemClass is "BOUGHT" or "MADE" or "PACKAGING" or "KIT" ? "Y" : "N";
        if (iv.Allowed == "Y" && iv.IsStocked != "Y")
        {
            iv.Allowed = "N";
            ca.SetMessage("Item " + Cob.Trim(iv.ItemCode) + " is not flagged as stocked, so it cannot hold a physical balance");
        }
        if (iv.Allowed != "Y" && Cob.Spaces(ca.Message))
        {
            ca.SetMessage("An item of class " + Cob.Trim(iv.ItemClass) + " never holds ordinary physical stock");
        }
    }

    // ------------------------------------------------------------------ 4000 VALIDATE

    private async Task ActivationReviewAsync(ItemvalArea iv, CancellationToken ct)
    {
        var ca = iv.Ca;
        await ReadItemAsync(iv, ct);
        if (ca.Failed) return;
        await ReadComplianceAsync(iv, ct);
        if (ca.Failed) return;
        iv.Faults.Clear();
        CheckIdentity(iv);
        await CheckUnitsAsync(iv, ct);
        CheckFinance(iv);
        CheckTrace(iv);
        await CheckVatAsync(iv, ct);
        await CheckCustomsAsync(iv, ct);
        CheckCompliance(iv);
        await CheckSourcesAsync(iv, ct);
        iv.Ready = iv.Faults.Any(f => f.Severity == "ERROR") ? "N" : "Y";
        if (iv.Ready == "Y")
        {
            ca.SetMessage("Item " + Cob.Trim(iv.ItemCode) + " has the information its enabled processes need");
            if (iv.Faults.Count > 0)
            {
                ca.ReturnCode = 4;
                ca.ReasonCode = "ITM0004";
            }
        }
        else
        {
            ca.ReasonCode = "ITM0004";
            ca.SetMessage("Activation review found " + Cob.IntEd(iv.Faults.Count) + " item(s) to resolve before release");
            Reject(ca);
        }
    }

    /// <summary>4050-ADD-FAULT (at most 24 lines).</summary>
    private void AddFault(ItemvalArea iv, string field, string text)
    {
        if (iv.Faults.Count < 24)
        {
            iv.Faults.Add(new ItemvalFault(Cob.X(_reqId, 8), Cob.X(_severity, 8), Cob.X(field, 24), Cob.X(text, 90)));
        }
    }

    /// <summary>MOVE "first " TO WS-TEXT; STRING FUNCTION TRIM(WS-TEXT) "second" (the leading part loses its trailing space).</summary>
    private static string Join(string first, string second) => Cob.X(Cob.Trim(Cob.X(first, 200)) + second, 200);

    private void CheckIdentity(ItemvalArea iv)
    {
        _reqId = "ITM-004";
        if (Cob.Spaces(iv.Description))
        {
            _severity = "ERROR";
            AddFault(iv, "Description", Join("An item must carry a description before it is ", "released"));
        }
        if (Cob.Spaces(iv.Steward))
        {
            _severity = "WARNING";
            AddFault(iv, "StewardUser", "No master data steward owns this item");
        }
        if (Cob.Spaces(iv.Category))
        {
            _severity = "WARNING";
            AddFault(iv, "Category", "No analysis category is set");
        }
    }

    private async Task CheckUnitsAsync(ItemvalArea iv, CancellationToken ct)
    {
        _reqId = "ITM-005";
        _severity = "ERROR";
        if (Cob.Spaces(iv.StockUom)) AddFault(iv, "StockUomCode", "A stock unit is required");
        _reqId = "ITM-006";
        if (iv.Quantum <= 0) AddFault(iv, "QuantumQty", Join("The permitted quantity increment must be ", "greater than zero"));
        if (iv.Traceability == "SERIAL" && iv.Quantum != 1) AddFault(iv, "QuantumQty", "A serialised item must move in whole units of 1");
        if (iv.IsPurchasable == "Y")
        {
            _reqId = "ITM-007";
            var count = await CountAsync(iv,
                "SELECT COUNT(*) FROM inv.ItemUom WHERE CompanyCode = @p1 AND ItemCode = @p2 AND UsageType = @p3",
                [iv.Ca.Company, iv.ItemCode, "PURCHASE"], zeroOnError: true, ct);
            if (count == 0)
            {
                _severity = "WARNING";
                AddFault(iv, "ItemUom PURCHASE", Join("No purchase unit conversion is recorded; ", " the stock unit will be assumed"));
            }
        }
    }

    private void CheckFinance(ItemvalArea iv)
    {
        _reqId = "ITM-010";
        _severity = "ERROR";
        if (iv.IsStocked == "Y" && Cob.Spaces(iv.ValuationGrp)) AddFault(iv, "ValuationGroup", "Stocked items need a valuation group");
        if (iv.IsStocked == "Y" && Cob.Spaces(_invAcctGrp)) AddFault(iv, "InventoryAccountGroup", "Stocked items need an inventory account mapping");
        if (iv.IsSaleable == "Y" && Cob.Spaces(_revAcctGrp))
        {
            _severity = "WARNING";
            AddFault(iv, "RevenueAccountGroup", "Saleable items should map to a revenue account");
        }
    }

    private void CheckTrace(ItemvalArea iv)
    {
        _reqId = "ITM-011";
        _severity = "ERROR";
        if (iv.ShelfLifeDays > 0 && _minShelf > iv.ShelfLifeDays)
        {
            AddFault(iv, "MinRemainingShelfLife", Join("The minimum remaining shelf life is longer ", " than the shelf life"));
        }
        if (iv.ShelfLifeDays > 0 && iv.Traceability == "NONE")
        {
            _severity = "WARNING";
            AddFault(iv, "TraceabilityType", Join("A shelf life cannot be enforced without lot or ", " serial control"));
        }
    }

    private async Task CheckVatAsync(ItemvalArea iv, CancellationToken ct)
    {
        _reqId = "ITM-025";
        _severity = "ERROR";
        const string vatSql =
            "SELECT COUNT(*) FROM inv.ItemVatClass WHERE CompanyCode = @p1 AND ItemCode = @p2 AND Direction = @p3 AND " +
            "EffectiveFrom <= CONVERT(date, @p4, 112) AND (EffectiveTo IS NULL OR EffectiveTo >= CONVERT(date, @p5, 112))";
        var date = Cob.D8(iv.Ca.BusinessDate);
        if (iv.IsSaleable == "Y")
        {
            var count = await CountAsync(iv, vatSql, [iv.Ca.Company, iv.ItemCode, "SALE", date, date], zeroOnError: true, ct);
            if (count == 0)
            {
                AddFault(iv, "ItemVatClass SALE", Join("A saleable item cannot be invoiced without ", " an effective UK VAT classification"));
            }
        }
        if (iv.IsPurchasable == "Y")
        {
            var count = await CountAsync(iv, vatSql, [iv.Ca.Company, iv.ItemCode, "PURCH", date, date], zeroOnError: true, ct);
            if (count == 0)
            {
                _severity = "WARNING";
                AddFault(iv, "ItemVatClass PURCH", "No purchase VAT liability is recorded");
            }
        }
    }

    private async Task CheckCustomsAsync(ItemvalArea iv, CancellationToken ct)
    {
        _reqId = "ITM-026";
        _severity = "WARNING";
        if (iv.ItemClass == "SERVICE") return;
        var date = Cob.D8(iv.Ca.BusinessDate);
        var count = await CountAsync(iv,
            "SELECT COUNT(*) FROM inv.ItemCustoms WHERE CompanyCode = @p1 AND ItemCode = @p2 AND EffectiveFrom <= CONVERT(date, @p3, 112) AND " +
            "(EffectiveTo IS NULL OR EffectiveTo >= CONVERT(date, @p4, 112))",
            [iv.Ca.Company, iv.ItemCode, date, date], zeroOnError: false, ct);
        if (count == 0)
        {
            AddFault(iv, "ItemCustoms", Join("No customs data; an international movement of ", " this item will be blocked"));
        }
    }

    private void CheckCompliance(ItemvalArea iv)
    {
        _reqId = "ITM-027";
        _severity = "ERROR";
        if (_ukca == "Y" && Cob.Spaces(_conformRef))
        {
            AddFault(iv, "ConformityDocRef", Join("A UKCA marked item needs its declaration of ", " conformity reference"));
        }
        if (!Cob.Spaces(_dgUn) && (Cob.Spaces(_dgClass) || Cob.Spaces(_dgPack)))
        {
            AddFault(iv, "DangerousGoods", Join("Dangerous goods need a class and a packing ", " group as well as the UN number"));
        }
        _severity = "WARNING";
        if (!Cob.Spaces(_battery) && Cob.Spaces(_weee))
        {
            AddFault(iv, "WeeeCategory", Join("A battery category without a WEEE category ", " looks incomplete"));
        }
    }

    private async Task CheckSourcesAsync(ItemvalArea iv, CancellationToken ct)
    {
        _reqId = "ITM-008";
        _severity = "WARNING";
        if (iv.IsPurchasable != "Y") return;
        var count = await CountAsync(iv,
            "SELECT COUNT(*) FROM inv.ItemPartner WHERE CompanyCode = @p1 AND ItemCode = @p2 AND PartnerType = 'SUPPLIER' AND IsApprovedSource = 1",
            [iv.Ca.Company, iv.ItemCode], zeroOnError: false, ct);
        if (count == 0) AddFault(iv, "ItemPartner", "No approved supplier source is recorded");
    }

    /// <summary>
    /// A COUNT(*) review query. On a SQL failure 9300-SQL-FAILED sets RC 12 and the review continues: the count is 0 where the
    /// program moved 0 (4250, 4550) and the check is skipped (null) where it went to the exit (4600, 4800).
    /// </summary>
    private async Task<decimal?> CountAsync(ItemvalArea iv, string text, object?[] args, bool zeroOnError, CancellationToken ct)
    {
        var r = await sql.SingleAsync(text, args, ct);
        if (r.IsError)
        {
            SqlFailed(iv.Ca, r);
            return zeroOnError ? 0 : null;
        }
        return r.NotFound ? 0 : Col.Num(r.Row, 0);
    }

    private void Reject(CaHeader ca)
    {
        _error = true;
        ca.ReturnCode = 8;
        ca.AddMessage();
    }

    private void SqlFailed(CaHeader ca, SqlioOutcome r)
    {
        _error = true;
        ca.SetMessage(r.Message200(Prog));
        ca.ReasonCode = "SQL0001";
        ca.ReturnCode = 12;
        ca.AddMessage();
    }
}
