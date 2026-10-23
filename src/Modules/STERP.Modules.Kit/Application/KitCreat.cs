using Sterp.Modules.Kit.Common;
using Sterp.Modules.Kit.Data;
using Sterp.Modules.Kit.Domain;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Kit.Application;

/// <summary>KITCREATCA (cobol/copy/KITCREATCA.cpy): the COMMAREA of KITCREAT.</summary>
internal sealed class KitCreatArea : KitArea
{
    public string KitRef { get; set; } = "";
    public string KitKind { get; set; } = "";
    public string ParentItem { get; set; } = "";
    public string ParentRev { get; set; } = "";
    public string ParentDesc { get; set; } = "";
    /// <summary>KC-SITE X(10) (the body field; CA-SITE is <see cref="KitArea.Site"/>).</summary>
    public string KcSite { get; set; } = "";
    public int BomId { get; set; }
    public string BomRef { get; set; } = "";
    public string BomRev { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceRef { get; set; } = "";
    public string JobRef { get; set; } = "";
    public string ParentKitRef { get; set; } = "";
    public decimal PlannedQty { get; set; }
    public decimal CompletedQty { get; set; }
    public decimal RejectedQty { get; set; }
    public int NeedDate { get; set; }
    public int StartDate { get; set; }
    public int CompleteDate { get; set; }
    public int SelectionDate { get; set; }
    public string Backflush { get; set; } = "";
    public string OwnerUser { get; set; } = "";
    public string Status { get; set; } = "";
    public string PrevStatus { get; set; } = "";
    public long ExecKitId { get; set; }
    public decimal GrossStarts { get; set; }
    public decimal Yield { get; set; }
    public decimal Quantum { get; set; }
    public decimal BatchSize { get; set; }
    public int RunCount { get; set; }
    public int BatchCount { get; set; }
    public string Consolidate { get; set; } = "";
    public string ExpandPhantom { get; set; } = "";
    public int LineTotal { get; set; }
    public int ShortageCnt { get; set; }
    public string Completeness { get; set; } = "";
    public decimal PlannedCost { get; set; }
    public decimal WipValue { get; set; }
    public long ApprovalId { get; set; }
    public string NeedsAuth { get; set; } = "";
    public string MayReceive { get; set; } = "";
    public string ReasonText { get; set; } = "";
    public string WarningText { get; set; } = "";
    public int FromLine { get; set; }
    public int WinMax { get; set; }
    public int WinCnt { get; set; }
    public string MoreLines { get; set; } = "";
    public KitCreatLine[] Lines { get; } = Enumerable.Range(0, 20).Select(_ => new KitCreatLine()).ToArray();
}

/// <summary>One KC-LINES occurrence.</summary>
internal sealed class KitCreatLine
{
    public int LineNum { get; set; }
    public string Path { get; set; } = "";
    public string Item { get; set; } = "";
    public string Rev { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Basis { get; set; } = "";
    public string Uom { get; set; } = "";
    public decimal Factor { get; set; }
    public decimal OrigReq { get; set; }
    public decimal CurrReq { get; set; }
    public decimal ScrapPct { get; set; }
    public decimal Reserved { get; set; }
    public decimal Picked { get; set; }
    public decimal GrossIss { get; set; }
    public decimal Returned { get; set; }
    public decimal Scrapped { get; set; }
    public decimal NetIss { get; set; }
    public decimal Remaining { get; set; }
    public decimal Excess { get; set; }
    public string Subst { get; set; } = "";
    public int NeedDate { get; set; }
    public string Location { get; set; } = "";
    public decimal UnitCost { get; set; }
    public string Status { get; set; } = "";

    public KitCreatLine Copy() => (KitCreatLine)MemberwiseClone();
}

/// <summary>
/// KITCREAT (cobol/online/kit/KITCREAT.cob) for the implemented functions REVIEW, RELEASE, ENQUIRE, GETLINE and KINDCHK
/// (CREATE is deferred, D-KIT-04). One instance per request: its WORKING-STORAGE (the KITDB control block, header, line and
/// item records) lives exactly as long as the ENQUIRE + GETLINE calls of one request (D-KIT-01, keep-stateless HD-16).
/// </summary>
internal sealed class KitCreat(KitDb kitDb, IPermissionService permissions, IAuditWriter audit)
{
    private readonly KitDbCtl _kd = new();
    private readonly KitHeader _kh = new();
    private readonly KitLine _kl = new();
    private readonly KitItem _ki = new();

    /// <summary>CALL "KITCREAT" USING KITCREATCA.</summary>
    public async Task LinkAsync(KitCreatArea ca, CancellationToken ct)
    {
        // Main-Entry
        ca.Program = "KITCREAT";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "KIT-002";
        ca.ReasonText = "";
        ca.WarningText = "";
        ca.NeedsAuth = "N";
        if (ca.WinMax is < 1 or > 20) ca.WinMax = 20;
        ca.WinCnt = 0;
        ca.MoreLines = "N";
        switch (ca.Function)
        {
            case "REVIEW": await ReviewAsync(ca, ct); break;
            case "RELEASE": await ReleaseAsync(ca, ct); break;
            case "ENQUIRE": await EnquireAsync(ca, ct); break;
            case "GETLINE": await PageKitLinesAsync(ca, ct); break;
            case "KINDCHK": await CheckKitKindAsync(ca, ct); break;
            default:
                // CREATE is not exposed (D-KIT-04); any other function is Reject-Function
                ca.Set(8, "KIT0002", Pic.Msg("KITCREAT does not support the function ", Pic.Trim(ca.Function)));
                break;
        }
    }

    private Task CallKitDbAsync(CancellationToken ct) => kitDb.CallAsync(_kd, _kh, _kl, _ki, ct);

    private void SetSqlFailure(KitCreatArea ca) => ca.Set(12, "SQL0001", _kd.ErrMsg);

    // ------------------------------------------------------------------ KINDCHK (KITCREAT.cob:119-175)

    private async Task CheckKitKindAsync(KitCreatArea ca, CancellationToken ct)
    {
        ca.MayReceive = "N";
        if (Pic.Blank(ca.ParentItem))
        {
            ca.Set(8, "KIT0003", "A parent item code is required.");
            return;
        }
        await LoadParentItemAsync(ca, ct);
        if (ca.Failed) return;
        switch (ca.KitKind)
        {
            case "COMMERCIAL":
                ca.MayReceive = "N";
                ca.ReasonText = "A commercial kit parent is a commercial identity, not an item balance.";
                break;
            case "PHANTOM":
                ca.MayReceive = "N";
                ca.ReasonText = "A phantom grouping has no receipt, no run and no separate stock.";
                break;
            case "TRIAL":
                ca.MayReceive = "N";
                ca.ReasonText = "A trial is a calculation and commits nothing.";
                break;
            default:
                if (_ki.Stocked)
                {
                    ca.MayReceive = "Y";
                    ca.ReasonText = "A stocked assembly may be completed into stock.";
                }
                else
                {
                    ca.MayReceive = "N";
                    ca.Set(4, "KIT0006", "This parent item does not hold stock and cannot be received as a finished assembly.");
                }
                break;
        }
        ca.ParentDesc = Pic.X(_ki.Description, 40);
    }

    private async Task LoadParentItemAsync(KitCreatArea ca, CancellationToken ct)
    {
        _kd.Initialize();
        _kd.Func = "ITEMGET";
        _kd.Company = ca.Company;
        _kd.ItemCode = Pic.X(ca.ParentItem, 40);
        await CallKitDbAsync(ct);
        switch (_kd.Rc)
        {
            case 0:
                break;
            case 100:
                ca.Set(16, "KIT0001", Pic.Msg("Item ", Pic.Trim(ca.ParentItem), " is not known in this company."));
                break;
            default:
                SetSqlFailure(ca);
                break;
        }
    }

    // ------------------------------------------------------------------ REVIEW (KITCREAT.cob:503-570)

    private async Task ReviewAsync(KitCreatArea ca, CancellationToken ct)
    {
        await LoadKitHeaderAsync(ca, ct);
        if (ca.Failed) return;
        if (!_kh.IsDraft)
        {
            ca.Set(8, "KIT0005", Pic.Msg("Only a draft kit may be reviewed; this kit is ", Pic.Trim(_kh.Status), "."));
            return;
        }
        await CheckLinesCompleteAsync(ca, ct);
        if (ca.Failed) return;
        _kd.NewStatus = "REVIEWED";
        await ChangeStatusAsync(ca, ct);
        ca.PrevStatus = "DRAFT";
        ca.Status = "REVIEWED";
        await AuditKitEventAsync(ca, ct);
    }

    private async Task CheckLinesCompleteAsync(KitCreatArea ca, CancellationToken ct)
    {
        var taken = 0;
        ca.ShortageCnt = 0;
        ca.Completeness = "COMPLETE";
        _kd.Initialize();
        _kd.Func = "LINOPEN";
        _kd.Company = ca.Company;
        _kd.KitId = _kh.ExecKitId;
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
            taken++;
            if (Pic.Blank(_kl.UomCode) || _kl.CurrentReq <= 0)
            {
                ca.Completeness = "INCOMPLETE";
                ca.ShortageCnt = (int)Pic.N(ca.ShortageCnt + 1, 4, 0);
            }
        }
        _kd.Func = "LINCLOS";
        await CallKitDbAsync(ct);
        ca.LineTotal = taken;
        if (taken == 0)
        {
            ca.Set(8, "KIT0004", "This kit has no material requirement lines.");
            return;
        }
        if (ca.Completeness != "COMPLETE")
        {
            ca.Set(8, "KIT0004", "One or more material lines have no unit or no requirement quantity.");
        }
    }

    // ------------------------------------------------------------------ RELEASE (KITCREAT.cob:576-646)

    private async Task ReleaseAsync(KitCreatArea ca, CancellationToken ct)
    {
        await LoadKitHeaderAsync(ca, ct);
        if (ca.Failed) return;
        if (!(_kh.IsReviewed || _kh.IsDraft))
        {
            ca.Set(8, "KIT0005", Pic.Msg("A kit in status ", Pic.Trim(_kh.Status), " cannot be released."));
            return;
        }
        await CheckReleaseAuthorityAsync(ca, ct);
        if (ca.Failed) return;
        await CheckLinesCompleteAsync(ca, ct);
        if (ca.Failed) return;
        await FreezeTheCalculationAsync(ca, ct);
        if (ca.Failed) return;
        _kd.NewStatus = "RELEASED";
        await ChangeStatusAsync(ca, ct);
        ca.PrevStatus = Pic.X(_kh.Status, 14);
        ca.Status = "RELEASED";
        await AuditKitEventAsync(ca, ct);
        ca.ReasonText = "Revisions, units and calculation assumptions are now fixed for this kit.";
    }

    /// <summary>PERMCHK CHECK KIT.EXEC.RELEASE at the kit's site: any non-zero answer is RC 6 KIT0009, which does not stop (SME-038).</summary>
    private async Task CheckReleaseAuthorityAsync(KitCreatArea ca, CancellationToken ct)
    {
        var caller = new KernelCaller(ca.Company, _kh.Site, ca.User, ca.BusinessDate, "");
        var perm = await permissions.CheckAsync(caller, "KIT.EXEC.RELEASE", ca.User, _kh.Site, ct);
        if (perm.Header.ReturnCode != 0)
        {
            ca.NeedsAuth = "Y";
            ca.Set(6, "KIT0009", Pic.Blank(Pic.X(perm.ReasonText, 200))
                ? "Releasing an execution kit needs the kit release authority."
                : Pic.X(perm.ReasonText, 200));
        }
    }

    private async Task FreezeTheCalculationAsync(KitCreatArea ca, CancellationToken ct)
    {
        _kd.Initialize();
        _kd.Func = "HDRFRZ";
        _kd.Company = ca.Company;
        _kd.User = ca.User;
        _kd.KitRef = _kh.KitRef;
        _kd.KitId = _kh.ExecKitId;
        _kd.Reason = Pic.Str(200, "Released from bill ", Pic.Trim(_kh.BomRev), " selected at ", Pic.D8(_kh.SelectionDate));
        await CallKitDbAsync(ct);
        if (_kd.Rc != 0) SetSqlFailure(ca);
    }

    private async Task ChangeStatusAsync(KitCreatArea ca, CancellationToken ct)
    {
        _kd.Func = "HDRSTAT";
        _kd.Company = ca.Company;
        _kd.User = ca.User;
        _kd.KitRef = _kh.KitRef;
        _kd.KitId = _kh.ExecKitId;
        await CallKitDbAsync(ct);
        if (_kd.Rc != 0) SetSqlFailure(ca);
    }

    /// <summary>Audit-Kit-Event: AUDITLOG WRITE (result not tested).</summary>
    private async Task AuditKitEventAsync(KitCreatArea ca, CancellationToken ct)
    {
        var caller = new KernelCaller(ca.Company, ca.KcSite, ca.User, ca.BusinessDate, ca.CorrelationId);
        var body = new AuditlogArea
        {
            EntityType = "KIT.EXECUTIONKIT",
            EntityKey = ca.KitRef,
            Action = ca.Function,
            FieldName = "Status",
            OldValue = ca.PrevStatus,
            NewValue = ca.Status,
            Reason = Pic.Pad(ca.ReasonText, 300)[..200],
            AuthorityRef = ca.SourceRef,
            Source = "KITCREAT",
            Sensitive = "N",
        };
        await audit.WriteAsync(caller, body, ct);
    }

    // ------------------------------------------------------------------ ENQUIRE / GETLINE (KITCREAT.cob:683-843)

    private async Task LoadKitHeaderAsync(KitCreatArea ca, CancellationToken ct)
    {
        if (Pic.Blank(ca.KitRef))
        {
            ca.Set(8, "KIT0003", "A kit reference is required.");
            return;
        }
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
                break;
            case 100:
                ca.Set(16, "KIT0001", Pic.Msg("Kit ", Pic.Trim(ca.KitRef), " is not known in this company."));
                break;
            default:
                SetSqlFailure(ca);
                break;
        }
    }

    private async Task EnquireAsync(KitCreatArea ca, CancellationToken ct)
    {
        await LoadKitHeaderAsync(ca, ct);
        if (ca.Failed) return;
        CopyHeaderOut(ca);
        await PageKitLinesAsync(ca, ct);
    }

    private void CopyHeaderOut(KitCreatArea ca)
    {
        ca.ExecKitId = _kh.ExecKitId;
        ca.KcSite = _kh.Site;
        ca.ParentItem = _kh.ParentItem;
        ca.ParentRev = _kh.ParentRev;
        ca.BomId = _kh.BomId;
        ca.BomRev = _kh.BomRev;
        ca.SourceType = _kh.SourceType;
        ca.SourceRef = _kh.SourceRef;
        ca.JobRef = Pic.X(_kh.JobRef, 20);
        ca.ParentKitRef = _kh.ParentKitRef;
        ca.PlannedQty = _kh.PlannedQty;
        ca.CompletedQty = _kh.CompletedQty;
        ca.RejectedQty = _kh.RejectedQty;
        ca.GrossStarts = _kh.GrossStarts;
        ca.Yield = _kh.Yield;
        ca.Quantum = _kh.Quantum;
        ca.BatchSize = _kh.BatchSize;
        ca.RunCount = _kh.RunCount;
        ca.BatchCount = _kh.BatchCount;
        ca.NeedDate = _kh.NeedDate;
        ca.StartDate = _kh.StartDate;
        ca.CompleteDate = _kh.PlanCompDate;
        ca.SelectionDate = _kh.SelectionDate;
        ca.OwnerUser = _kh.OwnerUser;
        ca.Status = _kh.Status;
        ca.Backflush = _kh.Backflush;
        ca.WipValue = _kh.WipValue;
        ca.ApprovalId = _kh.ApprovalId;
    }

    /// <summary>
    /// Page-Kit-Lines: KC-FROM-LINE is a 1-based inclusive position (&lt; 1 = 1), so a facade passing the count already
    /// returned restarts at the last row of the previous window (SME-644, reproduced).
    /// </summary>
    private async Task PageKitLinesAsync(KitCreatArea ca, CancellationToken ct)
    {
        if (_kh.ExecKitId == 0)
        {
            await LoadKitHeaderAsync(ca, ct);
            if (ca.Failed) return;
        }
        if (ca.FromLine < 1) ca.FromLine = 1;
        var taken = 0;
        ca.WinCnt = 0;
        ca.MoreLines = "N";
        _kd.Initialize();
        _kd.Func = "LINOPEN";
        _kd.Company = ca.Company;
        _kd.KitId = _kh.ExecKitId;
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
            taken++;
            if (taken < ca.FromLine) continue;
            if (ca.WinCnt < ca.WinMax)
            {
                ca.WinCnt++;
                FillWindowLine(ca, ca.Lines[ca.WinCnt - 1]);
            }
            else
            {
                ca.MoreLines = "Y";
                break;
            }
        }
        _kd.Func = "LINCLOS";
        await CallKitDbAsync(ct);
    }

    private void FillWindowLine(KitCreatArea ca, KitCreatLine w)
    {
        // Derive-Line-Quantities: KITQTY LINEQTY on an INITIALIZEd area, return code ignored
        var q = KitQty.LineQty(_kl.GrossIssued, _kl.ReturnedQty, _kl.ScrappedQty, _kl.CurrentReq);
        w.LineNum = _kl.LineNum;
        w.Path = _kl.PathRef;
        w.Item = _kl.ItemCode;
        w.Rev = _kl.Revision;
        w.Basis = _kl.UsageBasis;
        w.Uom = _kl.UomCode;
        w.Factor = _kl.Factor;
        w.OrigReq = _kl.OriginalReq;
        w.CurrReq = _kl.CurrentReq;
        w.ScrapPct = _kl.ScrapPct;
        w.Reserved = _kl.ReservedQty;
        w.Picked = _kl.PickedQty;
        w.GrossIss = _kl.GrossIssued;
        w.Returned = _kl.ReturnedQty;
        w.Scrapped = _kl.ScrappedQty;
        w.NetIss = q.NetIssued;
        w.Remaining = q.Remaining;
        w.Excess = q.Excess;
        w.Subst = _kl.Substitute;
        w.NeedDate = _kl.NeedDate;
        w.Location = _kl.Location;
        w.UnitCost = _kl.PlanUnitCost;
        w.Status = _kl.Status;
        w.Desc = "";
    }
}
