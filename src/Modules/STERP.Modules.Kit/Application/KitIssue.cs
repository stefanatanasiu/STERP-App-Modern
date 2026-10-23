using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Kit.Common;
using Sterp.Modules.Kit.Data;
using Sterp.Modules.Kit.Domain;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Kit.Application;

/// <summary>KITISSUECA (cobol/copy/KITISSUECA.cpy).</summary>
internal sealed class KitIssueArea : KitArea
{
    public string KitRef { get; set; } = "";
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public string Revision { get; set; } = "";
    public string PlannedItem { get; set; } = "";
    public string KsSite { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public string Location { get; set; } = "";
    public string LotCode { get; set; } = "";
    public string SerialNo { get; set; } = "";
    public decimal Quantity { get; set; }
    public string UomCode { get; set; } = "";
    public decimal GoodQty { get; set; }
    public int ActualDate { get; set; }
    public int PostingDate { get; set; }
    public string ReasonCodeIn { get; set; } = "";
    public string ReasonText { get; set; } = "";
    public long ApprovalId { get; set; }
    public long SubApproval { get; set; }
    public decimal SubRatio { get; set; }
    public string AllowOver { get; set; } = "";
    public string CheckOnly { get; set; } = "";
    public decimal StockQty { get; set; }
    public decimal Factor { get; set; }
    public decimal UnitCost { get; set; }
    public decimal ValueGbp { get; set; }
    public long KitIssueId { get; set; }
    public long MovementId { get; set; }
    public decimal LineGross { get; set; }
    public decimal LineNet { get; set; }
    public decimal LineRemaining { get; set; }
    public decimal LineExcess { get; set; }
    public string LineStatus { get; set; } = "";
    public string KitStatus { get; set; } = "";
    public decimal WipValue { get; set; }
    public decimal ShortageQty { get; set; }
    public decimal FreeQty { get; set; }
    public int LinesDone { get; set; }
    public int LinesFailed { get; set; }
    public string FailedItem { get; set; } = "";
    public string Traced { get; set; } = "";
    public string Duplicate { get; set; } = "";
    public string ImpactText { get; set; } = "";

    /// <summary>88 KS-IS-CHECK.</summary>
    public bool IsCheck => CheckOnly == "Y";
}

/// <summary>
/// KITISSUE (cobol/online/kit/KITISSUE.cob:103-957): ISSUE, CHECK and BACKFLSH (SUBST is not exposed, D-KIT-04). Every
/// statement commits on its own (no BEGN, spec 8.3); STKMOVE owns its movement transaction; the ITM contracts (AVAILQ,
/// STKMOVE, STKRESV) and EXCPRAIS run on the request's unit of work. One instance per request.
/// </summary>
internal sealed class KitIssue(KitDb kitDb, IAvailabilityService availq, IStockMovementService stkmove, IStockReservationService stkresv,
    IExceptionRaiser excprais)
{
    private readonly KitDbCtl _kd = new();
    private readonly KitHeader _kh = new();
    private readonly KitLine _kl = new();
    private readonly KitItem _ki = new();

    // Ws-Work
    private string _issueItem = "";
    private decimal _backflushQty;
    private decimal _usagePer;
    private int _done;
    private string _reqSite = "";
    private string _reqWarehouse = "";
    private string _reqLocation = "";
    private readonly List<int> _openLines = [];

    // the copies of the callee COMMAREAs the program keeps (AVAILQ answer used by Choose-Eligible-Bucket)
    private AvailqCommarea _avail = new();
    private decimal _smUnitCost;

    public async Task LinkAsync(KitIssueArea ca, CancellationToken ct)
    {
        ca.Program = "KITISSUE";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "KIT-010";
        ca.ImpactText = "";
        ca.FailedItem = "";
        ca.LinesDone = 0;
        ca.LinesFailed = 0;
        ca.KitIssueId = 0;
        ca.MovementId = 0;
        if (ca.ActualDate == 0) ca.ActualDate = ca.BusinessDate;
        if (ca.PostingDate == 0) ca.PostingDate = ca.ActualDate;
        switch (ca.Function)
        {
            case "ISSUE":
                await IssueOneComponentAsync(ca, ct);
                break;
            case "CHECK":
                ca.CheckOnly = "Y";
                await IssueOneComponentAsync(ca, ct);
                break;
            case "BACKFLSH":
                await BackflushTheKitAsync(ca, ct);
                break;
            default:
                // SUBST is not exposed (D-KIT-04); anything else is Reject-Function
                ca.Set(8, "KIT0002", Pic.Msg("KITISSUE does not support the function ", Pic.Trim(ca.Function)));
                break;
        }
    }

    private Task CallKitDbAsync(CancellationToken ct) => kitDb.CallAsync(_kd, _kh, _kl, _ki, ct);

    private void SetSqlFailure(KitIssueArea ca) => ca.Set(12, "SQL0001", _kd.ErrMsg);

    private static DateOnly? Date(int yyyymmdd) => CobolDate.FromYyyymmdd(yyyymmdd);

    // ------------------------------------------------------------------ loads

    private async Task LoadKitAsync(KitIssueArea ca, CancellationToken ct)
    {
        _kd.Initialize();
        _kh.Initialize();
        _kd.Func = "HDRGET";
        _kd.Company = ca.Company;
        _kd.User = ca.User;
        _kd.KitRef = Pic.X(ca.KitRef, 30);
        await CallKitDbAsync(ct);
        switch (_kd.Rc)
        {
            case 0:
                ca.KitStatus = Pic.X(_kh.Status, 14);
                ca.WipValue = _kh.WipValue;
                break;
            case 100:
                ca.Set(16, "KIT0001", Pic.Msg("Kit ", Pic.Trim(ca.KitRef), " is not known in this company."));
                break;
            default:
                SetSqlFailure(ca);
                break;
        }
        if (ca.ReturnCode == 0 && !_kh.MayIssue)
        {
            ca.Set(8, "KIT0005", Pic.Msg("Material cannot be issued to a kit in status ", Pic.Trim(_kh.Status), "."));
        }
        if (Pic.Blank(ca.KsSite)) ca.KsSite = _kh.Site;
    }

    private async Task LoadLineAsync(KitIssueArea ca, CancellationToken ct)
    {
        _kd.Func = "LINGET";
        _kd.KitId = _kh.ExecKitId;
        _kd.LineNum = ca.LineNum;
        _kl.Initialize();
        await CallKitDbAsync(ct);
        switch (_kd.Rc)
        {
            case 0:
                break;
            case 100:
                ca.Set(16, "KIT0007", "That material line is not on this kit.");
                break;
            default:
                SetSqlFailure(ca);
                break;
        }
    }

    /// <summary>Load-Issue-Item: the item actually issued decides the traceability rules.</summary>
    private async Task LoadIssueItemAsync(KitIssueArea ca, CancellationToken ct)
    {
        _kd.Func = "ITEMGET";
        _kd.ItemCode = _issueItem;
        await CallKitDbAsync(ct);
        if (_kd.Rc == 100)
        {
            ca.Set(16, "KIT0001", Pic.Msg("Item ", Pic.Trim(_issueItem), " is not known in this company."));
        }
        if (_kd.Rc != 0 && _kd.Rc != 100) SetSqlFailure(ca);
        ca.Traced = _ki.Traced;
    }

    // ------------------------------------------------------------------ ISSUE / CHECK

    private async Task IssueOneComponentAsync(KitIssueArea ca, CancellationToken ct)
    {
        await LoadKitAsync(ca, ct);
        if (ca.Failed) return;
        await LoadLineAsync(ca, ct);
        if (ca.Failed) return;
        DecideIssueItem(ca);
        if (ca.Failed) return;
        await LoadIssueItemAsync(ca, ct);
        if (ca.Failed) return;
        ValidateIssueQuantity(ca);
        if (ca.Failed) return;
        ValidateTraceability(ca);
        if (ca.Failed) return;
        CheckPermittedQuantity(ca);
        if (ca.Failed) return;
        await CheckFreeStockAsync(ca, ct);
        if (ca.Failed) return;
        if (ca.IsCheck)
        {
            ca.ImpactText = "Issue would be accepted.";
            return;
        }
        await PostStockIssueAsync(ca, ct);
        if (ca.Failed) return;
        await RecordKitIssueAsync(ca, ct);
        await UpdateLineQuantitiesAsync(ca, ct);
    }

    /// <summary>KIT-012: a substitute is issued against the planned line; the hold (RC 6) does not stop the issue.</summary>
    private void DecideIssueItem(KitIssueArea ca)
    {
        ca.PlannedItem = Pic.X(_kl.ItemCode, 40);
        if (ca.Function == "SUBST" || (!Pic.Blank(ca.ItemCode) && ca.ItemCode != _kl.ItemCode))
        {
            _issueItem = Pic.X(ca.ItemCode, 40);
            if (_kl.Substitute != _issueItem)
            {
                ca.Set(6, "KIT0014", Pic.Msg("Item ", Pic.Trim(_issueItem), " is not the approved substitute for this line."));
                return;
            }
            if (_kl.SubApproval == 0 && ca.SubApproval == 0)
            {
                ca.Set(6, "KIT0014", "This substitution has no recorded approval.");
                return;
            }
        }
        else
        {
            _issueItem = Pic.X(_kl.ItemCode, 40);
        }
        ca.ItemCode = _issueItem;
    }

    private void ValidateIssueQuantity(KitIssueArea ca)
    {
        if (ca.Quantity <= 0)
        {
            ca.Set(8, "KIT0003", "An issue quantity must be greater than zero.");
        }
        if (Pic.Blank(ca.UomCode)) ca.UomCode = Pic.X(_kl.UomCode, 10);
    }

    private void ValidateTraceability(KitIssueArea ca)
    {
        if (!_ki.IsTraced) return;
        if (_ki.LotTraced && Pic.Blank(ca.LotCode))
        {
            ca.Set(8, "KIT0012", Pic.Msg("Item ", Pic.Trim(_issueItem), " is lot controlled: an actual lot code is required."));
        }
        if (_ki.SerialTraced && Pic.Blank(ca.SerialNo))
        {
            ca.Set(8, "KIT0012", Pic.Msg("Item ", Pic.Trim(_issueItem), " is serial controlled: an actual serial number is required."));
        }
    }

    /// <summary>KIT-011: more than the remaining requirement needs a reason (KIT0008) and an approval (KIT0009 hold).</summary>
    private void CheckPermittedQuantity(KitIssueArea ca)
    {
        var q = KitQty.LineQty(_kl.GrossIssued, _kl.ReturnedQty, _kl.ScrappedQty, _kl.CurrentReq);
        ca.LineRemaining = q.Remaining;
        var wouldBeNet = Pic.Q(q.NetIssued + ca.Quantity);
        var overQty = Pic.Q(wouldBeNet - _kl.CurrentReq);
        if (overQty <= 0) return;
        if (Pic.Blank(ca.ReasonText))
        {
            ca.Set(8, "KIT0008", Pic.Msg("This issue exceeds the approved requirement by ", Pic.Ed(overQty), " and needs a visible reason."));
            return;
        }
        if (ca.AllowOver != "Y" || ca.ApprovalId == 0)
        {
            ca.Set(6, "KIT0009", Pic.Msg("Overissue of ", Pic.Ed(overQty), " needs approval before the material can be issued."));
        }
    }

    /// <summary>KIT-011/ITM-015: AVAILQ AVAIL - an approval never creates physical stock.</summary>
    private async Task CheckFreeStockAsync(KitIssueArea ca, CancellationToken ct)
    {
        _avail = new AvailqCommarea
        {
            ItemCode = _issueItem,
            Site = ca.KsSite,
            Warehouse = ca.Warehouse,
            Location = ca.Location,
            LotCode = ca.LotCode,
            SerialNo = ca.SerialNo,
            DemandType = "KIT",
            DemandRef = ca.KitRef,
            DemandLine = _kl.LineNum,
            AsAt = Date(ca.BusinessDate),
        };
        var r = await availq.ExecuteAsync(AvailqFunctions.Avail, _avail, new ItmCall(ca.Company, ca.KsSite, ca.User, Date(ca.BusinessDate), ""), ct);
        if (r.ReturnCode > 4)
        {
            ca.Set(r.ReturnCode, r.ReasonCode, r.Message);
            return;
        }
        ca.FreeQty = Pic.Q(_avail.QtyForDemand);
        if (_avail.QtyEligible < ca.Quantity)
        {
            ca.ShortageQty = Pic.Q(ca.Quantity - _avail.QtyEligible);
            ca.Set(8, "KIT0010", Pic.Msg("Insufficient eligible stock: short by ", Pic.Ed(ca.ShortageQty), ". An approval cannot create physical stock."));
            await RaiseShortageExceptionAsync(ca, ct);
        }
        else
        {
            ca.ShortageQty = 0m;
        }
    }

    private async Task RaiseShortageExceptionAsync(KitIssueArea ca, CancellationToken ct)
    {
        if (ca.IsCheck) return;
        var body = new ExcpraisArea
        {
            Module = "KIT",
            Severity = "ERROR",
            Category = "KIT MATERIAL SHORTAGE",
            MessageText = Pic.Pad(ca.Message, 200),
            DocumentType = "EXECUTIONKIT",
            DocumentRef = ca.KitRef,
            OwnerUser = _kh.OwnerUser,
            Priority = 2,
            Quantity = ca.ShortageQty,
        };
        await excprais.RaiseAsync(new KernelCaller(ca.Company, ca.KsSite, ca.User, ca.BusinessDate, ""), body, ct);
    }

    // ------------------------------------------------------------------ posting

    /// <summary>
    /// Derive-Line-Correlation (CR2741): the first 30 characters of CA-CORRELATION-ID, trimmed, + "-L" + the line number
    /// as PIC 9(4), cut to 36.
    /// </summary>
    private static string LineCorrelation(KitIssueArea ca, int lineNum)
    {
        var stem = Pic.Pad(ca.CorrelationId, 36)[..30];
        var suffix = Pic.X("-L" + Pic.Digits(lineNum, 4), 6);
        return Pic.Str(36, Pic.Trim(stem), Pic.Trim(suffix));
    }

    private async Task PostStockIssueAsync(KitIssueArea ca, CancellationToken ct)
    {
        var sm = new StkmoveCommarea
        {
            ItemCode = _issueItem,
            Revision = ca.Revision,
            Site = ca.KsSite,
            Warehouse = ca.Warehouse,
            Location = ca.Location,
            LotCode = ca.LotCode,
            SerialNo = ca.SerialNo,
            Quantity = ca.Quantity,
            UomCode = ca.UomCode,
            MovementDate = Date(ca.ActualDate),
            PostingDate = Date(ca.PostingDate),
            SourceModule = "KIT",
            SourceDocType = "EXECUTIONKIT",
            SourceDocRef = ca.KitRef,
            SourceDocLine = _kl.LineNum,
            ReasonCode = ca.ReasonCodeIn,
            Reason = Pic.Pad(ca.ReasonText, 300)[..200],
        };
        await PriceTheIssueAsync(ca, ct);
        sm.UnitCost = _smUnitCost;
        var call = new ItmCall(ca.Company, ca.KsSite, ca.User, Date(ca.BusinessDate), LineCorrelation(ca, _kl.LineNum));
        var r = await stkmove.ExecuteAsync(StkmoveFunctions.Issue, sm, call, ct);
        if (r.ReturnCode > 4)
        {
            ca.Set(r.ReturnCode, r.ReasonCode, r.Message);
            ca.FailedItem = _issueItem;
            ca.LinesFailed = (int)Pic.N(ca.LinesFailed + 1, 4, 0);
            return;
        }
        ca.StockQty = Pic.Q(sm.StockQty);
        ca.Factor = Pic.N(sm.Factor, 9, 9);
        ca.UnitCost = Pic.N(sm.UnitCost, 11, 6);
        ca.ValueGbp = Pic.M(sm.ValueGbp);
        ca.MovementId = sm.MovementId;
        ca.Duplicate = Pic.X(sm.Duplicate, 1);
    }

    /// <summary>CR2744: the item's cost at the issuing site - standard for a standard-costed item, else the average, else the standard.</summary>
    private async Task PriceTheIssueAsync(KitIssueArea ca, CancellationToken ct)
    {
        _smUnitCost = 0m;
        _kd.Func = "ITEMGET";
        _kd.Site = ca.KsSite;
        _kd.ItemCode = _issueItem;
        await CallKitDbAsync(ct);
        if (_kd.Rc != 0) return;
        if (_ki.StandardCosted && _ki.StdCost != 0) _smUnitCost = _ki.StdCost;
        else if (_ki.AvgCost != 0) _smUnitCost = _ki.AvgCost;
        else _smUnitCost = _ki.StdCost;
    }

    private async Task RecordKitIssueAsync(KitIssueArea ca, CancellationToken ct)
    {
        _kd.Func = "ISSINS";
        _kd.Company = ca.Company;
        _kd.User = ca.User;
        _kd.KitRef = Pic.X(ca.KitRef, 30);
        _kd.Reason = Pic.X(ca.CorrelationId, 200);
        _kd.IType = ca.Function == "BACKFLSH" ? "BACKFLUSH" : "ISSUE";
        _kd.IItem = _issueItem;
        _kd.IRev = ca.Revision;
        _kd.IQty = ca.Quantity;
        _kd.IUom = ca.UomCode;
        _kd.IStockQty = ca.StockQty;
        _kd.IWarehouse = ca.Warehouse;
        _kd.ILocation = ca.Location;
        _kd.ILot = ca.LotCode;
        _kd.ISerial = ca.SerialNo;
        _kd.IActDate = ca.ActualDate;
        _kd.IPostDate = ca.PostingDate;
        _kd.IUnitCost = ca.UnitCost;
        _kd.IValue = ca.ValueGbp;
        _kd.IReasonCode = ca.ReasonCodeIn;
        _kd.IReason = Pic.Pad(ca.ReasonText, 300)[..200];
        _kd.IApproval = ca.ApprovalId;
        _kd.IOrigIssue = 0;
        _kd.IMovement = ca.MovementId;
        await CallKitDbAsync(ct);
        if (_kd.Rc != 0)
        {
            SetSqlFailure(ca);
            return;
        }
        ca.KitIssueId = _kd.NewId;
        await ConsumeOpenPicksAsync(ca, ct);
    }

    /// <summary>KIT-009/KIT-010: the line's open picks are spent oldest first; no pick trail is not an error.</summary>
    private async Task ConsumeOpenPicksAsync(KitIssueArea ca, CancellationToken ct)
    {
        _kd.Func = "PCKUSE";
        _kd.Company = ca.Company;
        _kd.User = ca.User;
        _kd.KitRef = Pic.X(ca.KitRef, 30);
        _kd.LineNum = _kl.LineNum;
        _kd.PQty = ca.StockQty;
        await CallKitDbAsync(ct);
        if (_kd.Rc != 0) SetSqlFailure(ca);
    }

    private async Task UpdateLineQuantitiesAsync(KitIssueArea ca, CancellationToken ct)
    {
        _kl.GrossIssued = Pic.Q(_kl.GrossIssued + ca.StockQty);
        _kl.IssuedValue = Pic.R(_kl.IssuedValue + ca.ValueGbp, 11, 2);
        if (_kl.ReservedQty > 0) await ConsumeTheReservationAsync(ca, ct);
        var q = KitQty.LineQty(_kl.GrossIssued, _kl.ReturnedQty, _kl.ScrappedQty, _kl.CurrentReq);
        _kl.NetIssued = q.NetIssued;
        _kl.RemainingQty = q.Remaining;
        _kl.ExcessQty = q.Excess;
        _kl.Status = _kl.RemainingQty > 0 ? "PARTISSUED" : "ISSUED";
        _kd.Func = "LINSAVE";
        await CallKitDbAsync(ct);
        if (_kd.Rc != 0)
        {
            SetSqlFailure(ca);
            return;
        }
        ca.LineGross = _kl.GrossIssued;
        ca.LineNet = _kl.NetIssued;
        ca.LineRemaining = _kl.RemainingQty;
        ca.LineExcess = _kl.ExcessQty;
        ca.LineStatus = _kl.Status;
        ca.LinesDone = (int)Pic.N(ca.LinesDone + 1, 4, 0);
        await AdvanceKitStatusAsync(ca, ct);
        if (ca.LineExcess > 0)
        {
            ca.Set(4, "KIT0022", Pic.Msg("Issued. Excess material of ", Pic.Ed(ca.LineExcess), " is now on this line and needs a disposition."));
        }
    }

    /// <summary>KIT-008/ITM-014: STKRESV CONSUME of the planned item; a refusal gives up as much reservation as the line had.</summary>
    private async Task ConsumeTheReservationAsync(KitIssueArea ca, CancellationToken ct)
    {
        var sr = new StkresvCommarea
        {
            ItemCode = _kl.ItemCode,
            Revision = ca.Revision,
            Site = ca.KsSite,
            Warehouse = ca.Warehouse,
            Location = ca.Location,
            LotCode = ca.LotCode,
            SerialNo = ca.SerialNo,
            DemandType = "KIT",
            DemandRef = ca.KitRef,
            DemandLine = _kl.LineNum,
            QtyRequested = ca.StockQty,
        };
        var r = await stkresv.ExecuteAsync(StkresvFunctions.Consume, sr, new ItmCall(ca.Company, ca.KsSite, ca.User, Date(ca.BusinessDate), ca.CorrelationId), ct);
        if (r.ReturnCode > 4 || sr.QtyConsumed == 0)
        {
            _kl.ReservedQty = _kl.ReservedQty > ca.StockQty ? Pic.Q(_kl.ReservedQty - ca.StockQty) : 0m;
        }
        else
        {
            _kl.ReservedQty = Pic.Q(_kl.ReservedQty - sr.QtyConsumed);
        }
        if (_kl.ReservedQty < 0) _kl.ReservedQty = 0m;
    }

    /// <summary>A RELEASED kit becomes PARTISSUED with its first posted issue (HDRSTAT result not tested).</summary>
    private async Task AdvanceKitStatusAsync(KitIssueArea ca, CancellationToken ct)
    {
        if (!_kh.IsReleased) return;
        _kd.NewStatus = "PARTISSUED";
        _kd.Func = "HDRSTAT";
        _kd.Company = ca.Company;
        _kd.User = ca.User;
        _kd.KitRef = _kh.KitRef;
        await CallKitDbAsync(ct);
        ca.KitStatus = "PARTISSUED";
    }

    // ------------------------------------------------------------------ BACKFLSH (KITISSUE.cob:681-957)

    private async Task BackflushTheKitAsync(KitIssueArea ca, CancellationToken ct)
    {
        await LoadKitAsync(ca, ct);
        if (ca.Failed) return;
        if (!_kh.BackflushOn)
        {
            ca.Set(8, "KIT0013", "Backflush is not enabled on this kit; it must be explicitly authorised.");
            return;
        }
        if (ca.GoodQty <= 0)
        {
            ca.Set(8, "KIT0003", "A good parent quantity is required to backflush.");
            return;
        }
        _reqSite = ca.KsSite;
        _reqWarehouse = ca.Warehouse;
        _reqLocation = ca.Location;
        ca.CheckOnly = "Y";
        await RunBackflushPassAsync(ca, ct);
        if (ca.Failed)
        {
            ca.ImpactText = Pic.Str(200, "Backflush refused: no material was issued and no parent was received. First failure on ",
                Pic.Trim(ca.FailedItem), ".");
            return;
        }
        ca.CheckOnly = "N";
        ca.LinesDone = 0;
        await RunBackflushPassAsync(ca, ct);
        if (!ca.Failed)
        {
            ca.ImpactText = Pic.Str(200, "Backflush issued ", Pic.Digits(ca.LinesDone, 4), " component line(s) once for the good output.");
        }
    }

    private async Task RunBackflushPassAsync(KitIssueArea ca, CancellationToken ct)
    {
        _done = 0;
        await CollectOpenLinesAsync(ca, ct);
        if (ca.Failed) return;
        foreach (var lineNum in _openLines.ToList())
        {
            ca.LineNum = lineNum;
            await LoadLineAsync(ca, ct);
            if (ca.Failed) break;
            await BackflushOneLineAsync(ca, ct);
            if (ca.Failed) break;
        }
    }

    /// <summary>CR2742: the open lines are listed, and the cursor given back, before anything is posted.</summary>
    private async Task CollectOpenLinesAsync(KitIssueArea ca, CancellationToken ct)
    {
        _openLines.Clear();
        _kd.Initialize();
        _kd.Func = "LINOPEN";
        _kd.Company = ca.Company;
        _kd.User = ca.User;
        _kd.KitId = _kh.ExecKitId;
        _kd.OnlyOpen = "Y";
        await CallKitDbAsync(ct);
        if (_kd.Rc != 0)
        {
            SetSqlFailure(ca);
            return;
        }
        while (true)
        {
            _kd.Func = "LINNEXT";
            await CallKitDbAsync(ct);
            if (_kd.Rc != 0) break;
            if (_openLines.Count < 300)
            {
                _openLines.Add(_kl.LineNum);
            }
            else
            {
                ca.Set(8, "KIT0003", "This kit has more open lines than one backflush pass may carry; issue them in parts.");
                break;
            }
        }
        _kd.Func = "LINCLOS";
        await CallKitDbAsync(ct);
    }

    private async Task BackflushOneLineAsync(KitIssueArea ca, CancellationToken ct)
    {
        _issueItem = Pic.X(_kl.ItemCode, 40);
        if (!Pic.Blank(_kl.Substitute)) _issueItem = Pic.X(_kl.Substitute, 40);
        await LoadIssueItemAsync(ca, ct);
        if (ca.Failed)
        {
            ca.FailedItem = _issueItem;
            return;
        }
        if (_ki.IsTraced)
        {
            // D-20: backflush may not guess a lot or a serial
            ca.Set(8, "KIT0013", Pic.Msg("Item ", Pic.Trim(_issueItem), " is lot or serial controlled and cannot be backflushed."));
            ca.FailedItem = _issueItem;
            ca.LinesFailed = (int)Pic.N(ca.LinesFailed + 1, 4, 0);
            return;
        }
        if (_kh.PlannedQty <= 0) return;
        if (_kl.FixedPerRun || _kl.FixedPerBatch)
        {
            if (_kl.GrossIssued > 0) return;
            _backflushQty = _kl.CurrentReq;
        }
        else
        {
            _usagePer = CobolNumeric.Divide(_kl.CurrentReq, _kh.PlannedQty, 13, 6, rounded: true);
            _backflushQty = CobolNumeric.Multiply(_usagePer, ca.GoodQty, 13, 6, rounded: true);
        }
        var outstanding = Pic.Q(_kl.CurrentReq - _kl.NetIssued);
        if (outstanding < 0) outstanding = 0m;
        if (_backflushQty > outstanding) _backflushQty = outstanding;
        if (_backflushQty <= 0) return;
        ca.Quantity = _backflushQty;
        ca.LineNum = _kl.LineNum;
        ca.UomCode = Pic.X(_kl.UomCode, 10);
        ca.LotCode = "";
        ca.SerialNo = "";
        await SourceLineStockAsync(ca, ct);
        if (ca.Failed)
        {
            ca.FailedItem = _issueItem;
            return;
        }
        CheckPermittedQuantity(ca);
        if (ca.Failed)
        {
            ca.FailedItem = _issueItem;
            return;
        }
        await CheckFreeStockAsync(ca, ct);
        if (ca.Failed)
        {
            ca.FailedItem = _issueItem;
            return;
        }
        if (ca.IsCheck)
        {
            _done++;
            return;
        }
        await PostStockIssueAsync(ca, ct);
        if (ca.Failed)
        {
            ca.FailedItem = _issueItem;
            return;
        }
        await RecordKitIssueAsync(ca, ct);
        await UpdateLineQuantitiesAsync(ca, ct);
        _done++;
    }

    /// <summary>CR2742: the eligible balances decide where each line's material comes from; the caller's scope narrows the search.</summary>
    private async Task SourceLineStockAsync(KitIssueArea ca, CancellationToken ct)
    {
        ca.KsSite = _reqSite;
        ca.Warehouse = _reqWarehouse;
        ca.Location = _reqLocation;
        if (Pic.Blank(ca.KsSite)) ca.KsSite = _kh.Site;
        if (Pic.Blank(ca.Location)) ca.Location = Pic.X(_kl.Location, 20);
        _avail = new AvailqCommarea
        {
            ItemCode = _issueItem,
            Site = ca.KsSite,
            Warehouse = _reqWarehouse,
            Location = "",
            OwnerType = "OWNED",
            DemandType = "KIT",
            DemandRef = ca.KitRef,
            DemandLine = _kl.LineNum,
            AsAt = Date(ca.BusinessDate),
        };
        var r = await availq.ExecuteAsync(AvailqFunctions.Enquire, _avail, new ItmCall(ca.Company, ca.KsSite, ca.User, Date(ca.BusinessDate), ""), ct);
        if (r.ReturnCode > 4)
        {
            ca.Set(r.ReturnCode, r.ReasonCode, r.Message);
            return;
        }
        ChooseEligibleBucket(ca);
    }

    private void ChooseEligibleBucket(KitIssueArea ca)
    {
        AvailqLine? best = null;
        var bestFree = 0m;
        foreach (var b in _avail.Lines)
        {
            if (b.Eligible != "Y" || b.Free <= 0 || !Pic.Blank(b.Lot) || !Pic.Blank(b.Serial) || b.OwnerType != "OWNED") continue;
            if (!Pic.Blank(ca.Location) && b.Location == ca.Location && b.Free >= ca.Quantity)
            {
                best = b;
                break;
            }
            if (b.Free > bestFree)
            {
                best = b;
                bestFree = b.Free;
            }
        }
        if (best is null) return;
        ca.KsSite = Pic.X(best.Site, 10);
        ca.Warehouse = Pic.X(best.Warehouse, 20);
        ca.Location = Pic.X(best.Location, 20);
        ca.Revision = Pic.X(best.Revision, 4);
    }
}
