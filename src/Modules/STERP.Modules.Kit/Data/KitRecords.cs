namespace Sterp.Modules.Kit.Data;

/// <summary>
/// KIT-HEADER-REC of KITWORK.cpy (cobol/copy/KITWORK.cpy:62-118). Text fields right-trimmed; the constructor applies the
/// VALUE clauses (a program's initial WORKING-STORAGE), <see cref="Initialize"/> is COBOL INITIALIZE (spaces and zeros).
/// </summary>
internal sealed class KitHeader
{
    public long ExecKitId { get; set; }
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string KitRef { get; set; } = "";
    public string ParentItem { get; set; } = "";
    public string ParentRev { get; set; } = "";
    public int BomId { get; set; }
    public string BomRev { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceRef { get; set; } = "";
    public string JobRef { get; set; } = "";
    public string ParentKitRef { get; set; } = "";
    public decimal PlannedQty { get; set; }
    public decimal CompletedQty { get; set; }
    public decimal RejectedQty { get; set; }
    public decimal GrossStarts { get; set; }
    public decimal Yield { get; set; } = 1m;
    public decimal Quantum { get; set; }
    public decimal BatchSize { get; set; }
    public int RunCount { get; set; } = 1;
    public int BatchCount { get; set; }
    public int NeedDate { get; set; }
    public int StartDate { get; set; }
    public int PlanCompDate { get; set; }
    public int ActCompDate { get; set; }
    public int SelectionDate { get; set; }
    public string OwnerUser { get; set; } = "";
    public string Status { get; set; } = "";
    public string ReleasedBy { get; set; } = "";
    public long ApprovalId { get; set; }
    public decimal WipValue { get; set; }
    public string Backflush { get; set; } = "N";
    public int LineTotal { get; set; }

    public bool IsDraft => Status == "DRAFT";
    public bool IsReviewed => Status == "REVIEWED";
    public bool IsReleased => Status == "RELEASED";

    /// <summary>88 KH-MAY-ISSUE.</summary>
    public bool MayIssue => Status is "RELEASED" or "PARTISSUED" or "ISSUED" or "PARTCOMPLETE";

    /// <summary>88 KH-BACKFLUSH-ON.</summary>
    public bool BackflushOn => Backflush == "Y";

    public void Initialize()
    {
        ExecKitId = 0;
        Company = Site = KitRef = ParentItem = ParentRev = BomRev = SourceType = SourceRef = JobRef = ParentKitRef = "";
        BomId = 0;
        PlannedQty = CompletedQty = RejectedQty = GrossStarts = Yield = Quantum = BatchSize = 0m;
        RunCount = BatchCount = 0;
        NeedDate = StartDate = PlanCompDate = ActCompDate = SelectionDate = 0;
        OwnerUser = Status = ReleasedBy = Backflush = "";
        ApprovalId = 0;
        WipValue = 0m;
        LineTotal = 0;
    }
}

/// <summary>KIT-LINE-REC of KITWORK.cpy (KITWORK.cpy:124-160).</summary>
internal sealed class KitLine
{
    public long LineId { get; set; }
    public int LineNum { get; set; }
    public string PathRef { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public string Revision { get; set; } = "";
    public int BomCompId { get; set; }
    public string UsageBasis { get; set; } = "PROPORTION";
    public decimal OriginalReq { get; set; }
    public decimal CurrentReq { get; set; }
    public string UomCode { get; set; } = "";
    public decimal Factor { get; set; } = 1m;
    public decimal ScrapPct { get; set; }
    public decimal ReservedQty { get; set; }
    public decimal PickedQty { get; set; }
    public decimal GrossIssued { get; set; }
    public decimal ReturnedQty { get; set; }
    public decimal ScrappedQty { get; set; }
    public decimal NetIssued { get; set; }
    public decimal RemainingQty { get; set; }
    public decimal ExcessQty { get; set; }
    public string Substitute { get; set; } = "";
    public long SubApproval { get; set; }
    public string Deviation { get; set; } = "";
    public string DevApprover { get; set; } = "";
    public int NeedDate { get; set; }
    public string Location { get; set; } = "";
    public decimal PlanUnitCost { get; set; }
    public decimal IssuedValue { get; set; }
    public string Status { get; set; } = "OPEN";

    /// <summary>88 KL-FIXED-PER-RUN.</summary>
    public bool FixedPerRun => UsageBasis == "FIXEDRUN";

    /// <summary>88 KL-FIXED-PER-BATCH.</summary>
    public bool FixedPerBatch => UsageBasis == "FIXEDBATCH";

    public void Initialize()
    {
        LineId = 0;
        LineNum = 0;
        PathRef = ItemCode = Revision = UsageBasis = UomCode = Substitute = Deviation = DevApprover = Location = Status = "";
        BomCompId = 0;
        OriginalReq = CurrentReq = Factor = ScrapPct = ReservedQty = PickedQty = GrossIssued = ReturnedQty = ScrappedQty = 0m;
        NetIssued = RemainingQty = ExcessQty = PlanUnitCost = IssuedValue = 0m;
        SubApproval = 0;
        NeedDate = 0;
    }

    public KitLine Copy() => (KitLine)MemberwiseClone();
}

/// <summary>KIT-ITEM-REC of KITWORK.cpy (KITWORK.cpy:166-184).</summary>
internal sealed class KitItem
{
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string ItemClass { get; set; } = "";
    public string StockUom { get; set; } = "";
    public string IsStocked { get; set; } = "N";
    public string LotControl { get; set; } = "N";
    public string SerialControl { get; set; } = "N";
    public string Traced { get; set; } = "N";
    public decimal StdCost { get; set; }
    public decimal AvgCost { get; set; }
    public string Status { get; set; } = "";
    public string CostMethod { get; set; } = "";

    public bool Stocked => IsStocked == "Y";
    public bool LotTraced => LotControl == "Y";
    public bool SerialTraced => SerialControl == "Y";
    public bool IsTraced => Traced == "Y";

    /// <summary>88 KI-STANDARD-COSTED.</summary>
    public bool StandardCosted => CostMethod == "STD";
}

/// <summary>
/// KIT-DB-CTL of KITDBW.cpy (cobol/copy/KITDBW.cpy:45-119): the control block a caller fills before CALL "KITDB". Values
/// persist between calls exactly as the caller left them (callers INITIALIZE it at chosen points).
/// </summary>
internal sealed class KitDbCtl
{
    public string Func { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public string KitRef { get; set; } = "";
    public long KitId { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public string NewStatus { get; set; } = "";
    public string Reason { get; set; } = "";
    public string OnlyOpen { get; set; } = "N";
    public int Rc { get; set; }
    public int Rows { get; set; }
    public string ErrMsg { get; set; } = "";
    public long NewId { get; set; }

    // KD-ISS (ISSINS)
    public string IType { get; set; } = "";
    public string IItem { get; set; } = "";
    public string IRev { get; set; } = "";
    public decimal IQty { get; set; }
    public string IUom { get; set; } = "";
    public decimal IStockQty { get; set; }
    public string IWarehouse { get; set; } = "";
    public string ILocation { get; set; } = "";
    public string ILot { get; set; } = "";
    public string ISerial { get; set; } = "";
    public int IActDate { get; set; }
    public int IPostDate { get; set; }
    public decimal IUnitCost { get; set; }
    public decimal IValue { get; set; }
    public string IReasonCode { get; set; } = "";
    public string IReason { get; set; } = "";
    public long IApproval { get; set; }
    public long IOrigIssue { get; set; }
    public long IMovement { get; set; }

    // KD-PCK (PCKUSE)
    public decimal PQty { get; set; }
    public decimal PConsumed { get; set; }

    /// <summary>INITIALIZE KIT-DB-CTL: alphanumerics to spaces, numerics to zero.</summary>
    public void Initialize()
    {
        Func = Company = Site = User = KitRef = ItemCode = NewStatus = Reason = OnlyOpen = ErrMsg = "";
        KitId = 0;
        LineNum = 0;
        Rc = Rows = 0;
        NewId = 0;
        IType = IItem = IRev = IUom = IWarehouse = ILocation = ILot = ISerial = IReasonCode = IReason = "";
        IQty = IStockQty = IUnitCost = IValue = 0m;
        IActDate = IPostDate = 0;
        IApproval = IOrigIssue = IMovement = 0;
        PQty = PConsumed = 0m;
    }
}
