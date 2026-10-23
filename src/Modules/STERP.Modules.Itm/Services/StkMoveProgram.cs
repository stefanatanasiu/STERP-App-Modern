using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Data;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Itm.Services;

/// <summary>
/// STKMOVE (cobol/online/itm/STKMOVE.cob; FR-STERP-ITM-020, STERP-ITM.md section 4.1): the only way physical stock changes.
/// Validates, gates the item through ITEMVAL CLASSCHK, converts through UOMCONV, checks and registers lots/serials through
/// LOTSER and posts every leg through inv.usp_PostStockMovement on the caller's engine connection.
/// </summary>
internal sealed class StkMoveProgram(Sqlio sql, ItemValProgram itemVal, UomConvProgram uomConv, LotSerProgram lotSer, ItmDateProgram itmDate)
{
    private const string Prog = "STKMOVE";

    // WS-HDR
    private string _co = "";
    private string _siteIn = "";
    private string _user = "";
    private int _bdate;
    private string _corr = "";
    private string _func = "";

    // WS-CONTROL
    private bool _error;
    private int _rc;
    private string _reason = "";
    private string _msg = "";
    private string _lifecycle = "";
    private string _stockUom = "";
    private string _trace = "";
    private string _direction = "";
    private string _moveType = "";
    private string _postSite = "";
    private string _postWhs = "";
    private string _postLoc = "";
    private string _postCond = "";
    private decimal _postQty;
    private string _postCounter = "";
    private string _postCorr = "";
    private int _leg;
    private decimal _valueIn;
    private bool _ownTran;

    // SM date fields as PIC 9(8)
    private int _movementDate;
    private int _postingDate;
    private int _expiryDate;

    /// <summary>
    /// SM-EXPIRY-DATE as the PIC 9(8) a COBOL caller moved in, when it is not a calendar date the public area can hold
    /// (OPENSTK accepts 19000101..29991231 numerically, SME-1498); it reaches LOTSER NEWLOT unchanged.
    /// </summary>
    internal int? RawExpiryDate { get; set; }

    public async Task RunAsync(CaHeader ca, StkmoveCommarea sm, CancellationToken ct)
    {
        try
        {
            await Initialise(ca, sm, ct);
            Validate(sm);
            if (_error)
            {
                Report(ca, sm);
                return;
            }
            await ReadItemAsync(sm, ct);
            if (_error)
            {
                Report(ca, sm);
                return;
            }
            await BeginMovementAsync(ct);
            if (_error)
            {
                Report(ca, sm);
                return;
            }
            switch (_func)
            {
                case "RECEIPT" or "ISSUE" or "RETURN" or "ADJUST" or "COMPLETE" or "OPENING" or "SCRAP":
                    await SimpleMoveAsync(ca, sm, ct);
                    break;
                case "TRANSFER": await TransferAsync(ca, sm, ct); break;
                case "CONFIRM": await ConfirmAsync(ca, sm, ct); break;
                case "HOLD": await HoldAsync(ca, sm, ct); break;
                case "RELEASE": await ReleaseHoldAsync(ca, sm, ct); break;
                default:
                    _reason = "STK0008";
                    _msg = Cob.Str(200, "STKMOVE does not support function ", Cob.Trim(_func));
                    Reject();
                    break;
            }
            await EndMovementAsync(ct);
            Report(ca, sm);
        }
        finally
        {
            sm.MovementDate = Cob.Date(_movementDate);
            sm.PostingDate = Cob.Date(_postingDate);
            sm.ExpiryDate = Cob.Date(_expiryDate);
        }
    }

    // ------------------------------------------------------------------ 0100

    private async Task Initialise(CaHeader ca, StkmoveCommarea sm, CancellationToken ct)
    {
        _error = false;
        _rc = 0;
        _reason = "";
        _msg = "";
        ca.Program = Prog;
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "ITM-015";
        _co = ca.Company;
        _siteIn = ca.Site;
        _user = ca.User;
        _bdate = ca.BusinessDate;
        _corr = ca.CorrelationId;
        _func = Cob.Upper(ca.Function, 8);
        NormaliseInputs(sm);
        sm.StockQty = 0;
        sm.Factor = 0;
        sm.MovementId = 0;
        sm.MovementId2 = 0;
        sm.QtyBefore = 0;
        sm.QtyAfter = 0;
        _valueIn = sm.ValueGbp;
        sm.ValueGbp = 0;
        if (_func != "RELEASE") sm.HoldId = 0;
        sm.MovementType = "";
        sm.TransitLoc = "";
        sm.Narrative = "";
        sm.Duplicate = "N";
        sm.ItemCode = Cob.Upper(sm.ItemCode, 40);
        sm.UomCode = Cob.Upper(sm.UomCode, 10);
        sm.LotCode = Cob.Upper(sm.LotCode, 30);
        sm.SerialNo = Cob.Upper(sm.SerialNo, 40);
        if (Cob.Spaces(sm.Site)) sm.Site = _siteIn;
        if (Cob.Spaces(sm.OwnerType)) sm.OwnerType = "OWNED";
        if (Cob.Spaces(sm.Condition)) sm.Condition = "ACCEPTED";
        if (Cob.Spaces(sm.SourceModule)) sm.SourceModule = "ITM";
        _movementDate = Cob.Date8(sm.MovementDate);
        _postingDate = Cob.Date8(sm.PostingDate);
        _expiryDate = RawExpiryDate ?? Cob.Date8(sm.ExpiryDate);
        if (_movementDate == 0) _movementDate = _bdate;
        if (_movementDate == 0)
        {
            var itd = new ItdBlock { Func = "TODAY" };
            await itmDate.CallAsync(itd, ct);
            _movementDate = itd.DateR;
        }
        if (_postingDate == 0) _postingDate = _movementDate;
        if (Cob.Spaces(sm.Warehouse)) sm.Warehouse = "MAIN";
        if (Cob.Spaces(sm.ToWarehouse)) sm.ToWarehouse = sm.Warehouse;
        if (Cob.Spaces(sm.ToSite)) sm.ToSite = sm.Site;
    }

    /// <summary>The COMMAREA field widths and numeric pictures (what the LINKAGE area can hold).</summary>
    private static void NormaliseInputs(StkmoveCommarea sm)
    {
        sm.ItemCode = Cob.X(sm.ItemCode, 40);
        sm.Revision = Cob.X(sm.Revision, 4);
        sm.Site = Cob.X(sm.Site, 10);
        sm.Warehouse = Cob.X(sm.Warehouse, 20);
        sm.Location = Cob.X(sm.Location, 20);
        sm.ToSite = Cob.X(sm.ToSite, 10);
        sm.ToWarehouse = Cob.X(sm.ToWarehouse, 20);
        sm.ToLocation = Cob.X(sm.ToLocation, 20);
        sm.LotCode = Cob.X(sm.LotCode, 30);
        sm.SerialNo = Cob.X(sm.SerialNo, 40);
        sm.OwnerType = Cob.X(sm.OwnerType, 10);
        sm.OwnerCode = Cob.X(sm.OwnerCode, 20);
        sm.Condition = Cob.X(sm.Condition, 12);
        sm.ToCondition = Cob.X(sm.ToCondition, 12);
        sm.Quantity = Cob.Q(sm.Quantity);
        sm.UomCode = Cob.X(sm.UomCode, 10);
        sm.UnitCost = CobolNumeric.Store(sm.UnitCost, 11, 6);
        sm.ValueGbp = CobolNumeric.Store(sm.ValueGbp, 11, 2);
        sm.ReasonCode = Cob.X(sm.ReasonCode, 20);
        sm.Reason = Cob.X(sm.Reason, 200);
        sm.ApprovalRef = Cob.X(sm.ApprovalRef, 40);
        sm.SourceModule = Cob.X(sm.SourceModule, 6);
        sm.SourceDocType = Cob.X(sm.SourceDocType, 20);
        sm.SourceDocRef = Cob.X(sm.SourceDocRef, 40);
        sm.ViaTransit = Cob.X(sm.ViaTransit, 1);
        sm.HoldType = Cob.X(sm.HoldType, 12);
        sm.HoldOutcome = Cob.X(sm.HoldOutcome, 12);
    }

    // ------------------------------------------------------------------ 0200

    private void Validate(StkmoveCommarea sm)
    {
        if (Cob.Spaces(_co) || Cob.Spaces(sm.ItemCode))
        {
            _reason = "STK0001";
            _msg = "A company and an item are required";
            Reject();
            return;
        }
        if (Cob.Spaces(sm.Site) || Cob.Spaces(sm.Location))
        {
            _reason = "STK0002";
            _msg = "A site and a stock location are required";
            Reject();
            return;
        }
        if (sm.Quantity == 0)
        {
            _reason = "STK0003";
            _msg = "A movement must state a quantity";
            Reject();
            return;
        }
        if (_func != "ADJUST" && sm.Quantity < 0)
        {
            _reason = "STK0003";
            _msg = "Only an adjustment may carry a negative quantity; the movement type gives the direction";
            Reject();
            return;
        }
        if (_func is "ADJUST" or "SCRAP" or "HOLD" or "RELEASE" or "OPENING" && Cob.Spaces(sm.Reason) && Cob.Spaces(sm.ReasonCode))
        {
            _reason = "STK0007";
            _msg = Cob.Str(200, "A ", Cob.Trim(_func), " must record why it was made");
            Reject();
        }
    }

    // ------------------------------------------------------------------ 0300 ITEMVAL CLASSCHK STOCK

    private async Task ReadItemAsync(StkmoveCommarea sm, CancellationToken ct)
    {
        var iv = new ItemvalArea
        {
            Ca = CaHeader.Init("CLASSCHK", _co, _siteIn, _user, _bdate),
            ItemCode = sm.ItemCode,
            Action = "STOCK",
        };
        await itemVal.RunAsync(iv, ct);
        _lifecycle = iv.Lifecycle;
        _stockUom = iv.StockUom;
        _trace = iv.Traceability;
        if (iv.Ca.ReturnCode == 16)
        {
            _reason = "STK0001";
            _msg = iv.Ca.Message;
            Reject();
            return;
        }
        if (iv.Ca.ReturnCode == 12)
        {
            _reason = "SQL0001";
            _msg = iv.Ca.Message;
            _rc = 12;
            _error = true;
            return;
        }
        if (iv.Allowed != "Y")
        {
            _reason = "STK0012";
            _msg = iv.Ca.Message;
            Reject();
            return;
        }
        if (_lifecycle is "DRAFT" or "REVIEW")
        {
            _reason = "STK0001";
            _msg = Cob.Str(200, "Item ", Cob.Trim(sm.ItemCode), " is still a draft and cannot take stock until it has been reviewed and released");
            Reject();
            return;
        }
        if (_lifecycle == "RETIRED")
        {
            _reason = "STK0001";
            _msg = Cob.Str(200, "Item ", Cob.Trim(sm.ItemCode), " is retired; historical enquiry only");
            Reject();
            return;
        }
        if (_lifecycle == "BLOCKED" && _func == "RECEIPT")
        {
            _reason = "STK0001";
            _msg = Cob.Str(200, "Item ", Cob.Trim(sm.ItemCode), " is blocked for new supply; an authorised resolution is needed");
            _rc = 6;
            _error = true;
            return;
        }
        if (Cob.Spaces(sm.Revision)) sm.Revision = iv.CurrentRev;
    }

    // ------------------------------------------------------------------ 0400 UOMCONV TOSTOCK

    private async Task ConvertUnitsAsync(StkmoveCommarea sm, CancellationToken ct)
    {
        var ca = CaHeader.Init("TOSTOCK", _co, "", _user, _movementDate);
        if (Cob.Spaces(sm.UomCode)) sm.UomCode = _stockUom;
        var uc = new UomconvCommarea
        {
            ItemCode = sm.ItemCode,
            FromUom = sm.UomCode,
            UsageType = _func switch
            {
                "RECEIPT" => "PURCHASE",
                "RETURN" => "SALES",
                "ISSUE" => "ISSUE",
                _ => "STOCK",
            },
            EffDate = Cob.Date(_movementDate),
            EnteredQty = Math.Abs(sm.Quantity),
        };
        await uomConv.RunAsync(ca, uc, ct);
        sm.Factor = uc.Factor;
        _reason = ca.ReasonCode switch
        {
            "UOM0006" => "STK0004",
            "UOM0003" or "UOM0004" or "UOM0005" => "STK0003",
            _ => "",
        };
        if (ca.ReturnCode > 4)
        {
            if (Cob.Spaces(_reason)) _reason = "STK0003";
            _msg = ca.Message;
            Reject();
            return;
        }
        sm.StockQty = uc.StockQty;
        if (sm.Quantity < 0) sm.StockQty = Cob.Q(0 - sm.StockQty);
        if (sm.UnitCost != 0) sm.ValueGbp = CobolNumeric.Store(sm.StockQty * sm.UnitCost, 11, 2, rounded: true);
    }

    // ------------------------------------------------------------------ 0500 LOTSER CHKMOVE

    private async Task TraceCheckAsync(CaHeader caOut, StkmoveCommarea sm, CancellationToken ct)
    {
        var ca = CaHeader.Init("CHKMOVE", _co, "", _user, _movementDate);
        var ls = new LotserCommarea
        {
            ItemCode = sm.ItemCode,
            Revision = sm.Revision,
            LotCode = sm.LotCode,
            SerialNo = sm.SerialNo,
            Site = sm.Site,
            Warehouse = sm.Warehouse,
            Location = sm.Location,
            Direction = _direction,
            Quantity = Math.Abs(sm.StockQty),
            AsAt = Cob.Date(_movementDate),
        };
        await lotSer.RunAsync(ca, ls, ct);
        if (ca.ReturnCode > 4)
        {
            _reason = ca.ReasonCode switch
            {
                "LOT0001" or "LOT0002" => "STK0005",
                "LOT0003" => "STK0004",
                "LOT0004" => "STK0010",
                "LOT0005" => "STK0006",
                "LOT0007" or "LOT0008" or "LOT0009" => "STK0011",
                _ => "STK0005",
            };
            _msg = ca.Message;
            _rc = ca.ReturnCode;
            _error = true;
            caOut.AddMessage();
        }
    }

    // ------------------------------------------------------------------ 0600 LOTSER NEWLOT / NEWSER

    private async Task RegisterTraceAsync(StkmoveCommarea sm, CancellationToken ct)
    {
        if (_direction != "I") return;
        if (_trace == "LOT" && !Cob.Spaces(sm.LotCode))
        {
            var ca = CaHeader.Init("NEWLOT", _co, "", _user, _movementDate);
            var ls = new LotserCommarea
            {
                ItemCode = sm.ItemCode,
                Revision = sm.Revision,
                LotCode = sm.LotCode,
                ExpiryDate = Cob.Date(_expiryDate),
                ReceivedDate = Cob.Date(_movementDate),
                AsAt = Cob.Date(_movementDate),
                Condition = sm.Condition,
            };
            lotSer.RawExpiryDate = _expiryDate;
            try
            {
                await lotSer.RunAsync(ca, ls, ct);
            }
            finally
            {
                lotSer.RawExpiryDate = null;
            }
            if (ca.ReturnCode > 4)
            {
                _reason = "STK0005";
                _msg = ca.Message;
                Reject();
                return;
            }
            _expiryDate = lotSer.ExpiryOut;
        }
        if (_trace == "SERIAL" && !Cob.Spaces(sm.SerialNo))
        {
            var ca = CaHeader.Init("NEWSER", _co, "", _user, _movementDate);
            var ls = new LotserCommarea
            {
                ItemCode = sm.ItemCode,
                Revision = sm.Revision,
                LotCode = sm.LotCode,
                SerialNo = sm.SerialNo,
                Site = _postSite,
                Location = _postLoc,
                SerialStatus = "INSTOCK",
            };
            await lotSer.RunAsync(ca, ls, ct);
            if (ca.ReturnCode > 4)
            {
                _reason = "STK0011";
                _msg = ca.Message;
                Reject();
            }
        }
    }

    // ------------------------------------------------------------------ 0700 inv.usp_PostStockMovement

    private async Task PostAsync(StkmoveCommarea sm, CancellationToken ct)
    {
        if (_error) return;
        string? Opt(string v) => Cob.Spaces(v) ? null : v;
        var counterSite = "";
        if (_moveType == "TRANSFEROUT")
        {
            counterSite = _func == "TRANSFER" && sm.ViaTransit == "Y" ? sm.Site : sm.ToSite;
        }
        if (_moveType == "TRANSFERIN") counterSite = sm.Site;
        object?[] args =
        [
            _co, _postSite, _postWhs, _postLoc, sm.ItemCode, _moveType,
            Cob.SqlNum(Math.Abs(_postQty)),
            Cob.D8(_movementDate),
            _user, sm.Revision, sm.LotCode, sm.SerialNo, sm.OwnerType, sm.OwnerCode, _postCond, _direction, sm.UomCode,
            Cob.SqlNum(Math.Abs(sm.Quantity)),
            Cob.SqlNum(sm.Factor),
            sm.UnitCost == 0 ? null : Cob.SqlNum(sm.UnitCost),
            Cob.D8(_postingDate),
            sm.SourceModule,
            Opt(sm.SourceDocType),
            Opt(sm.SourceDocRef),
            sm.SourceDocLine == 0 ? null : Cob.SqlInt(sm.SourceDocLine),
            Opt(_postCounter),
            Opt(sm.ReasonCode),
            Opt(sm.Reason),
            Opt(sm.ApprovalRef),
            Opt(_postCorr),
            _valueIn == 0 ? null : Cob.SqlNum(Math.Abs(_valueIn)),
            Opt(counterSite),
        ];
        var r = await sql.SingleAsync(
            "EXEC inv.usp_PostStockMovement @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, @p16, " +
            "@p17, @p18, @p19, @p20, @p21, @p22, @p23, @p24, @p25, @p26, @p27, @p28, @p29, @p30, @p31, @p32",
            args, ct);
        if (r.IsError)
        {
            SqlFailed(r);
            return;
        }
        if (r.NotFound) return;
        var row = r.Row;
        if (_leg < 2)
        {
            sm.MovementId = (long)CobolNumeric.Store(Col.Num(row, 0), 17, 0);
            sm.QtyBefore = Cob.Q(Col.Num(row, 4));
            sm.QtyAfter = Cob.Q(Col.Num(row, 5));
        }
        else
        {
            sm.MovementId2 = (long)CobolNumeric.Store(Col.Num(row, 0), 17, 0);
            sm.QtyAfter = Cob.Q(Col.Num(row, 5));
        }
        if (Col.Bit(row, 9)) sm.Duplicate = "Y";
        sm.ValueGbp = CobolNumeric.Store(Math.Abs(Col.Num(row, 10)), 11, 2);
        sm.UnitCost = CobolNumeric.Store(Col.Num(row, 11), 11, 6);
        if (_moveType == "TRANSFEROUT") _valueIn = Math.Abs(sm.ValueGbp);
        sm.MovementType = _moveType;
    }

    // ------------------------------------------------------------------ 0800 / 0850 unit of work

    private async Task BeginMovementAsync(CancellationToken ct)
    {
        _ownTran = false;
        var uow = sql.UnitOfWork;
        try
        {
            if (uow.TransactionState == 1)
            {
                await uow.BeginAsync(ct);
                _ownTran = true;
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            SqlFailed(Failure(ex));
            return;
        }
        var r = BeginFailure ?? await sql.ExecAsync("IF @@TRANCOUNT = 0 BEGIN TRANSACTION; SAVE TRANSACTION STKMOVE", [], ct);
        if (r.IsError)
        {
            SqlFailed(r);
            await EndMovementAsync(ct);
        }
    }

    /// <summary>
    /// The SQLIO outcome of the first statement of the movement's unit when the calling batch program still holds an open
    /// cursor on the shared connection: under MARS the server refuses the transaction (COUNTADJ POST, verified on the legacy
    /// runtime). Set by the in-process COBOL caller only; null = execute the statement.
    /// </summary>
    internal SqlioOutcome? BeginFailure { get; set; }

    private SqlioOutcome Failure(Microsoft.Data.SqlClient.SqlException ex) =>
        SqlioOutcome.Failed(new Sterp.Platform.Kernel.SqlioException(sql.Runner.DriverText(ex),
            Sterp.Platform.Kernel.Data.LegacyDriverText.SqlState(ex), ex));

    private async Task EndMovementAsync(CancellationToken ct)
    {
        var uow = sql.UnitOfWork;
        if (_ownTran)
        {
            _ownTran = false;
            try
            {
                if (_error) await uow.RollbackAsync(ct);
                else await uow.CommitAsync(ct);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex)
            {
                if (!_error) SqlFailed(Failure(ex));
            }
            return;
        }
        if (!_error) return;
        // after a server-side XACT_ABORT rollback the connection is in a new unit (ODBC manual commit): the savepoint is gone
        // and the statement's error is ignored, as legacy
        _ = await sql.ExecAsync("IF XACT_STATE() = 1 ROLLBACK TRANSACTION STKMOVE", [], ct);
    }

    // ------------------------------------------------------------------ 1000 one-leg movement types

    private async Task SimpleMoveAsync(CaHeader ca, StkmoveCommarea sm, CancellationToken ct)
    {
        _leg = 1;
        switch (_func)
        {
            case "RECEIPT": _moveType = "RECEIPT"; _direction = "I"; break;
            case "RETURN": _moveType = "RETURNIN"; _direction = "I"; break;
            case "COMPLETE": _moveType = "COMPLETION"; _direction = "I"; break;
            case "OPENING": _moveType = "OPENING"; _direction = "I"; break;
            case "ISSUE": _moveType = "ISSUE"; _direction = "O"; break;
            case "SCRAP": _moveType = "SCRAP"; _direction = "O"; break;
            case "ADJUST":
                if (sm.Quantity < 0) { _moveType = "ADJUSTDOWN"; _direction = "O"; }
                else { _moveType = "ADJUSTUP"; _direction = "I"; }
                break;
        }
        await ConvertUnitsAsync(sm, ct);
        if (_error) return;
        await TraceCheckAsync(ca, sm, ct);
        if (_error) return;
        _postSite = sm.Site;
        _postWhs = sm.Warehouse;
        _postLoc = sm.Location;
        _postCond = sm.Condition;
        _postQty = sm.StockQty;
        _postCounter = "";
        _postCorr = Cob.X(_corr, 40);
        await RegisterTraceAsync(sm, ct);
        await PostAsync(sm, ct);
        if (_error) return;
        await SerialAfterAsync(sm, ct);
        _msg = Cob.Str(200, Cob.Trim(_func), " of ", Cob.QtyEd(sm.StockQty), " ", Cob.Trim(_stockUom), " ", Cob.Trim(sm.ItemCode),
            " at ", Cob.Trim(sm.Site), "/", Cob.Trim(sm.Location));
    }

    /// <summary>1900-SERIAL-AFTER: the serial's status after the legs (outcome not examined).</summary>
    private async Task SerialAfterAsync(StkmoveCommarea sm, CancellationToken ct)
    {
        if (_trace != "SERIAL" || Cob.Spaces(sm.SerialNo)) return;
        var ca = CaHeader.Init("SETSTAT", _co, "", _user, 0);
        var ls = new LotserCommarea { ItemCode = sm.ItemCode, SerialNo = sm.SerialNo };
        if (_direction == "I")
        {
            ls.SerialStatus = "INSTOCK";
            ls.Site = _postSite;
            ls.Location = _postLoc;
        }
        else
        {
            ls.SerialStatus = _moveType == "SCRAP" ? "SCRAPPED" : "ISSUED";
            ls.Site = "";
            ls.Location = "";
        }
        await lotSer.RunAsync(ca, ls, ct);
    }

    // ------------------------------------------------------------------ 2000 TRANSFER

    private async Task TransferAsync(CaHeader ca, StkmoveCommarea sm, CancellationToken ct)
    {
        if (sm.Site == sm.ToSite && sm.Warehouse == sm.ToWarehouse && sm.Location == sm.ToLocation)
        {
            _reason = "STK0009";
            _msg = "The origin and the destination of a transfer must be different";
            Reject();
            return;
        }
        if (Cob.Spaces(sm.ToLocation))
        {
            _reason = "STK0002";
            _msg = "A transfer must name its destination location";
            Reject();
            return;
        }
        _direction = "O";
        await ConvertUnitsAsync(sm, ct);
        if (_error) return;
        await TraceCheckAsync(ca, sm, ct);
        if (_error) return;
        if (sm.ViaTransit == "Y")
        {
            await FindTransitAsync(sm, ct);
            if (_error) return;
        }
        _leg = 1;
        _moveType = "TRANSFEROUT";
        _direction = "O";
        _postSite = sm.Site;
        _postWhs = sm.Warehouse;
        _postLoc = sm.Location;
        _postCond = sm.Condition;
        _postQty = sm.StockQty;
        _postCounter = sm.ViaTransit == "Y" ? sm.TransitLoc : sm.ToLocation;
        _postCorr = Cob.X(_corr, 40);
        await PostAsync(sm, ct);
        if (_error) return;
        _leg = 2;
        _moveType = "TRANSFERIN";
        _direction = "I";
        _postCounter = sm.Location;
        _postCorr = Cob.Spaces(_corr) ? "" : Cob.X(Cob.Trim(_corr) + "-IN", 40);
        if (sm.ViaTransit == "Y")
        {
            _postSite = sm.Site;
            _postWhs = sm.Warehouse;
            _postLoc = sm.TransitLoc;
        }
        else
        {
            _postSite = sm.ToSite;
            _postWhs = sm.ToWarehouse;
            _postLoc = sm.ToLocation;
        }
        await RegisterTraceAsync(sm, ct);
        await PostAsync(sm, ct);
        if (_error) return;
        await SerialAfterAsync(sm, ct);
        _msg = sm.ViaTransit == "Y"
            ? Cob.Str(200, "Transferred ", Cob.QtyEd(sm.StockQty), " ", Cob.Trim(sm.ItemCode), " into transit at ", Cob.Trim(sm.TransitLoc),
                "; confirm the receipt at ", Cob.Trim(sm.ToSite))
            : Cob.Str(200, "Transferred ", Cob.QtyEd(sm.StockQty), " ", Cob.Trim(sm.ItemCode), " from ", Cob.Trim(sm.Location), " to ",
                Cob.Trim(sm.ToLocation));
    }

    /// <summary>2100-FIND-TRANSIT: the site's first active TRANSIT location; its warehouse replaces SM-WAREHOUSE (SME-180).</summary>
    private async Task FindTransitAsync(StkmoveCommarea sm, CancellationToken ct)
    {
        var r = await sql.SingleAsync(
            "SELECT TOP 1 LocationCode, WarehouseCode FROM inv.Location WHERE CompanyCode = @p1 AND SiteCode = @p2 AND " +
            "LocationType = 'TRANSIT' AND IsActive = 1 ORDER BY WarehouseCode, LocationCode",
            [_co, sm.Site], ct);
        if (r.IsError)
        {
            SqlFailed(r);
            return;
        }
        if (r.NotFound)
        {
            _reason = "STK0013";
            _msg = Cob.Str(200, "Site ", Cob.Trim(sm.Site), " has no transit location, so a staged transfer cannot be recorded");
            Reject();
            return;
        }
        sm.TransitLoc = Cob.X(Col.Text(r.Row, 0), 20);
        sm.Warehouse = Cob.X(Col.Text(r.Row, 1), 20);
    }

    // ------------------------------------------------------------------ 3000 CONFIRM

    private async Task ConfirmAsync(CaHeader ca, StkmoveCommarea sm, CancellationToken ct)
    {
        _ = ca;
        await FindTransitAsync(sm, ct);
        if (_error) return;
        if (Cob.Spaces(sm.ToLocation))
        {
            _reason = "STK0002";
            _msg = "Name the destination location that is receiving the transfer";
            Reject();
            return;
        }
        _direction = "O";
        await ConvertUnitsAsync(sm, ct);
        if (_error) return;
        await CheckTransitQtyAsync(sm, ct);
        if (_error) return;
        _leg = 1;
        _moveType = "TRANSFEROUT";
        _direction = "O";
        _postSite = sm.Site;
        _postWhs = sm.Warehouse;
        _postLoc = sm.TransitLoc;
        _postCond = sm.Condition;
        _postQty = sm.StockQty;
        _postCounter = sm.ToLocation;
        _postCorr = Cob.X(_corr, 40);
        await PostAsync(sm, ct);
        if (_error) return;
        _leg = 2;
        _moveType = "TRANSFERIN";
        _direction = "I";
        _postSite = sm.ToSite;
        _postWhs = sm.ToWarehouse;
        _postLoc = sm.ToLocation;
        _postCounter = sm.TransitLoc;
        _postCorr = Cob.Spaces(_corr) ? "" : Cob.X(Cob.Trim(_corr) + "-RX", 40);
        await PostAsync(sm, ct);
        if (_error) return;
        await SerialAfterAsync(sm, ct);
        _msg = Cob.Str(200, "Confirmed receipt of ", Cob.QtyEd(sm.StockQty), " ", Cob.Trim(sm.ItemCode), " at ", Cob.Trim(sm.ToSite), "/",
            Cob.Trim(sm.ToLocation));
    }

    /// <summary>3100-CHECK-TRANSIT-QTY.</summary>
    private async Task CheckTransitQtyAsync(StkmoveCommarea sm, CancellationToken ct)
    {
        var r = await sql.SingleAsync(
            "SELECT ISNULL(SUM(QtyOnHand),0) FROM inv.StockBalance WHERE CompanyCode = @p1 AND ItemCode = @p2 AND SiteCode = @p3 AND " +
            "LocationCode = @p4 AND (@p5 = '' OR LotCode = @p6) AND (@p7 = '' OR SerialNo = @p8)",
            [_co, sm.ItemCode, sm.Site, sm.TransitLoc, sm.LotCode, sm.LotCode, sm.SerialNo, sm.SerialNo], ct);
        if (r.IsError)
        {
            SqlFailed(r);
            return;
        }
        var transitQty = r.NotFound ? 0m : Cob.Q(Col.Num(r.Row, 0));
        if (transitQty < sm.StockQty)
        {
            _reason = "STK0006";
            _msg = Cob.Str(200, "Only ", Cob.QtyEd(transitQty), " of this item is in transit at ", Cob.Trim(sm.TransitLoc));
            Reject();
        }
    }

    // ------------------------------------------------------------------ 4000 HOLD

    private async Task HoldAsync(CaHeader ca, StkmoveCommarea sm, CancellationToken ct)
    {
        _ = ca;
        if (Cob.Spaces(sm.ToCondition)) sm.ToCondition = "QUARANTINE";
        if (sm.ToCondition == sm.Condition)
        {
            _reason = "STK0014";
            _msg = "A hold must move the stock to a different quality condition";
            Reject();
            return;
        }
        if (Cob.Spaces(sm.HoldType)) sm.HoldType = "QUARANTINE";
        _direction = "O";
        await ConvertUnitsAsync(sm, ct);
        if (_error) return;
        _leg = 1;
        _moveType = "QUALITY";
        _direction = "O";
        _postSite = sm.Site;
        _postWhs = sm.Warehouse;
        _postLoc = sm.Location;
        _postCond = sm.Condition;
        _postQty = sm.StockQty;
        _postCounter = "";
        _postCorr = Cob.X(_corr, 40);
        await PostAsync(sm, ct);
        if (_error) return;
        _leg = 2;
        _direction = "I";
        _postCond = sm.ToCondition;
        _postCorr = Cob.Spaces(_corr) ? "" : Cob.X(Cob.Trim(_corr) + "-QH", 40);
        await PostAsync(sm, ct);
        if (_error) return;
        await WriteHoldAsync(sm, ct);
        _msg = Cob.Str(200, Cob.QtyEd(sm.StockQty), " of ", Cob.Trim(sm.ItemCode), " moved from ", Cob.Trim(sm.Condition), " to ",
            Cob.Trim(sm.ToCondition));
    }

    /// <summary>4100-WRITE-HOLD: INSERT inv.QualityHold ... OUTPUT inserted.HoldId.</summary>
    private async Task WriteHoldAsync(StkmoveCommarea sm, CancellationToken ct)
    {
        var r = await sql.SingleAsync(
            "INSERT inv.QualityHold (CompanyCode, SiteCode, ItemCode, LotCode, SerialNo, LocationCode, Quantity, HoldType, ReasonCode, " +
            "Reason, RaisedBy, SourceDocType, SourceDocRef, Status) OUTPUT inserted.HoldId VALUES (@p1, @p2, @p3, NULLIF(@p4,''), " +
            "NULLIF(@p5,''), @p6, @p7, @p8, NULLIF(@p9,''), @p10, @p11, NULLIF(@p12,''), NULLIF(@p13,''), 'OPEN')",
            [_co, sm.Site, sm.ItemCode, sm.LotCode, sm.SerialNo, sm.Location, Cob.SqlNum(sm.StockQty), sm.HoldType, sm.ReasonCode,
             sm.Reason, _user, sm.SourceDocType, sm.SourceDocRef], ct);
        if (r.IsError)
        {
            SqlFailed(r);
            return;
        }
        if (r.NotFound) return;
        sm.HoldId = (int)CobolNumeric.Store(Col.Num(r.Row, 0), 9, 0);
    }

    // ------------------------------------------------------------------ 5000 RELEASE

    private async Task ReleaseHoldAsync(CaHeader ca, StkmoveCommarea sm, CancellationToken ct)
    {
        _ = ca;
        if (sm.HoldId == 0)
        {
            _reason = "STK0014";
            _msg = "Name the hold that is being released";
            Reject();
            return;
        }
        if (Cob.Spaces(sm.HoldOutcome)) sm.HoldOutcome = "ACCEPT";
        await ReadHoldAsync(sm, ct);
        if (_error) return;
        if (Cob.Spaces(sm.ToCondition))
        {
            sm.ToCondition = sm.HoldOutcome switch
            {
                "ACCEPT" => "ACCEPTED",
                "REJECT" => "REJECTED",
                "SCRAP" => "REJECTED",
                _ => "ACCEPTED",
            };
        }
        _direction = "O";
        await ConvertUnitsAsync(sm, ct);
        if (_error) return;
        _leg = 1;
        _moveType = "QUALITY";
        _direction = "O";
        _postSite = sm.Site;
        _postWhs = sm.Warehouse;
        _postLoc = sm.Location;
        _postCond = sm.Condition;
        _postQty = sm.StockQty;
        _postCounter = "";
        _postCorr = Cob.X(_corr, 40);
        await PostAsync(sm, ct);
        if (_error) return;
        _leg = 2;
        _direction = "I";
        _postCond = sm.ToCondition;
        _postCorr = Cob.Spaces(_corr) ? "" : Cob.X(Cob.Trim(_corr) + "-QR", 40);
        await PostAsync(sm, ct);
        if (_error) return;
        await CloseHoldAsync(sm, ct);
        _msg = Cob.Str(200, "Hold released with outcome ", Cob.Trim(sm.HoldOutcome), " for ", Cob.QtyEd(sm.StockQty), " ", Cob.Trim(sm.ItemCode));
    }

    /// <summary>5100-READ-HOLD.</summary>
    private async Task ReadHoldAsync(StkmoveCommarea sm, CancellationToken ct)
    {
        var r = await sql.SingleAsync(
            "SELECT SiteCode, ItemCode, ISNULL(LotCode,''), ISNULL(SerialNo,''), ISNULL(LocationCode,''), Quantity, Status, HoldType " +
            "FROM inv.QualityHold WHERE CompanyCode = @p1 AND HoldId = @p2",
            [_co, Cob.SqlInt(sm.HoldId)], ct);
        if (r.IsError)
        {
            SqlFailed(r);
            return;
        }
        if (r.NotFound)
        {
            _reason = "STK0014";
            _msg = "That quality hold does not exist";
            Reject();
            return;
        }
        var row = r.Row;
        if (Col.Text(row, 6) == "CLOSED")
        {
            _reason = "STK0014";
            _msg = "That quality hold has already been released";
            Reject();
            return;
        }
        sm.Site = Cob.X(Col.Text(row, 0), 10);
        sm.ItemCode = Cob.X(Col.Text(row, 1), 40);
        sm.LotCode = Cob.X(Col.Text(row, 2), 30);
        sm.SerialNo = Cob.X(Col.Text(row, 3), 40);
        sm.Location = Cob.X(Col.Text(row, 4), 20);
        var holdQty = Cob.Q(Col.Num(row, 5));
        sm.HoldType = Cob.X(Col.Text(row, 7), 12);
        if (sm.Quantity == 0 || sm.Quantity > holdQty) sm.Quantity = holdQty;
        if (sm.Condition == "ACCEPTED") sm.Condition = sm.HoldType == "INSPECTION" ? "INSPECTION" : "QUARANTINE";
    }

    /// <summary>5200-CLOSE-HOLD.</summary>
    private async Task CloseHoldAsync(StkmoveCommarea sm, CancellationToken ct)
    {
        var r = await sql.ExecAsync(
            "UPDATE inv.QualityHold SET Status = 'CLOSED', Outcome = @p1, OutcomeQty = @p2, ReleasedBy = @p3, ReleasedAt = SYSDATETIME(), " +
            "AuthorityRef = NULLIF(@p4,'') WHERE CompanyCode = @p5 AND HoldId = @p6",
            [sm.HoldOutcome, Cob.SqlNum(sm.StockQty), _user, sm.ApprovalRef, _co, Cob.SqlInt(sm.HoldId)], ct);
        if (r.IsError) SqlFailed(r);
    }

    // ------------------------------------------------------------------ 9000 .. 9500

    private void Reject()
    {
        _error = true;
        _rc = 8;
    }

    /// <summary>
    /// 9300-SQL-FAILED: message = SQLIO-ERRMSG(1:200); "STERP-E-" in bytes 1-8 is a procedure refusal (RC 08, 0015 STK0006,
    /// 0027 STK0005, 0028 STK0004, other STK0001); anything else is RC 12 SQL0001. The driver prefix the platform reproduces
    /// (SME-011, verified) means procedure refusals arrive as RC 12 with the full driver text.
    /// </summary>
    private void SqlFailed(SqlioOutcome r)
    {
        _error = true;
        _msg = r.Message200(Prog);
        if (_msg.StartsWith("STERP-E-", StringComparison.Ordinal))
        {
            _rc = 8;
            var code = _msg.Length >= 13 ? _msg.Substring(8, 5) : "";
            _reason = code switch
            {
                "0015:" => "STK0006",
                "0027:" => "STK0005",
                "0028:" => "STK0004",
                _ => "STK0001",
            };
        }
        else
        {
            _reason = "SQL0001";
            _rc = 12;
        }
    }

    private void Report(CaHeader ca, StkmoveCommarea sm)
    {
        ca.ReturnCode = _rc;
        ca.ReasonCode = Cob.X(_reason, 8);
        ca.SetMessage(_msg);
        sm.Narrative = Cob.X(Cob.X(_msg, 200), 100);
        if (_rc > 0) ca.AddMessage();
    }
}
