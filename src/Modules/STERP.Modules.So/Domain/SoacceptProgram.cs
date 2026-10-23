using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Itm.Contracts;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Domain;

/// <summary>
/// SOACCEPT (cobol/online/so/SOACCEPT.cob, FR-SOACCEPT; FR-STERP-SO-017): VALIDATE (findings, no business effect) and ACCEPT
/// (validation, CREDCHK at ORDER in its own unit, the release decision, one BEGN..UPDATE so.SalesOrder..COMT, then AUDITLOG
/// autocommit). RELEASE and DEPOSIT have no production caller (D-SO-01, dispositions).
/// </summary>
public sealed class SoacceptProgram(
    ISqlRunner sql, IUnitOfWork uow, IUnitConversionService uomconv, ICreditCheckService credchk, IAuditWriter audit)
{
    private const string OrderSql =
        "SELECT CONVERT(varchar(24), o.SalesOrderId), " +
        "o.CustomerCode, ISNULL(o.InvoiceCustomerCode,''), " +
        "o.OrderType, CONVERT(char(8), o.OrderDate, 112), " +
        "o.CurrencyCode, CONVERT(varchar(24), o.FxRate), " +
        "ISNULL(o.TermsCode,''), " +
        "ISNULL(o.SalespersonCode,''), " +
        "ISNULL(o.DeliveryAddressRef,''), " +
        "ISNULL(o.DeliveryAddressSnapshot,''), " +
        "ISNULL(o.IncotermsRule,''), " +
        "ISNULL(o.IncotermsNamedPlace,''), " +
        "ISNULL(o.TermsAndConditionsVersion,''), " +
        "CONVERT(varchar(1), o.RetentionOfTitle), " +
        "ISNULL(o.VatTreatmentBasis,''), " +
        "ISNULL(o.CustomerVatRegNo,''), " +
        "ISNULL(o.VatLocation,''), " +
        "CONVERT(varchar(24), o.NetAmount), " +
        "CONVERT(varchar(24), o.VatAmount), " +
        "CONVERT(varchar(24), o.GrossAmount), " +
        "CONVERT(varchar(24), o.GrossAmountGBP), " +
        "o.CommercialStatus, o.ReleaseStatus, " +
        "o.FulfilmentStatus, o.BillingStatus, " +
        "ISNULL(o.HoldReason,''), CONVERT(varchar(24), " +
        "ISNULL(o.DepositRequiredGBP,0)), " +
        "CONVERT(varchar(24), o.DepositReceivedGBP), " +
        "o.SiteCode, CONVERT(varchar(10), (SELECT COUNT(*) " +
        "FROM so.SalesOrderLine l WHERE l.SalesOrderId = " +
        "o.SalesOrderId AND l.Status <> 'CANCELLED')) FROM " +
        "so.SalesOrder o WHERE o.CompanyCode = ? AND " +
        "o.OrderNumber = ?";

    private static readonly int[] OrderWidths =
        [24, 20, 20, 12, 8, 3, 24, 12, 20, 10, 300, 3, 60, 20, 1, 20, 15, 3, 24, 24, 24, 24, 12, 14, 14, 14, 200, 24, 24, 10, 10];

    private const string LinesSql =
        "SELECT CONVERT(varchar(10), l.LineNum), " +
        "ISNULL(l.ItemCode,''), ISNULL(l.Revision,''), " +
        "ISNULL(l.UomCode,''), CONVERT(varchar(24), " +
        "l.AcceptedQty), ISNULL(l.PriceSource,''), " +
        "ISNULL(l.VatCode,''), l.LineType, l.Status, " +
        "CONVERT(varchar(24), l.UnitPrice) FROM " +
        "so.SalesOrderLine l WHERE l.CompanyCode = ? AND " +
        "l.OrderNumber = ? AND l.Status <> 'CANCELLED' " +
        "ORDER BY l.LineNum";

    private static readonly int[] LineWidths = [10, 40, 4, 10, 24, 20, 4, 10, 10, 24];

    private const string ItemSql =
        "SELECT i.LifecycleStatus, ISNULL(i.SalesUomCode, " +
        "i.StockUomCode), CONVERT(varchar(1), " +
        "i.IsSaleable), ISNULL((SELECT TOP (1) r.Status " +
        "FROM inv.ItemRevision r WHERE r.CompanyCode = " +
        "i.CompanyCode AND r.ItemCode = i.ItemCode AND " +
        "r.Revision = ?),'') FROM inv.Item i WHERE " +
        "i.CompanyCode = ? AND i.ItemCode = ?";

    private static readonly int[] ItemWidths = [10, 10, 1, 10];

    private const string AcceptSql =
        "UPDATE so.SalesOrder SET CommercialStatus = " +
        "'ACCEPTED', ReleaseStatus = ?, HoldReason = " +
        "NULLIF(?,''), CreditOverrideId = NULLIF(CONVERT(" +
        "int, ?),0), ApprovalId = NULLIF(CONVERT(bigint, " +
        "?),0), ModifiedBy = ?, ModifiedAt = SYSDATETIME() " +
        "WHERE CompanyCode = ? AND OrderNumber = ? AND " +
        "CommercialStatus IN ('DRAFT','ACCEPTED','AMENDED')";

    private sealed class LineRow
    {
        public long Num;
        public string Item = "";
        public string Rev = "";
        public string Uom = "";
        public decimal Qty;
        public string PriceSource = "";
        public string VatCode = "";
        public string Type = "";
        public string Status = "";
    }

    /// <summary>WORKING-STORAGE of one LINK (every group INITIALIZEd in 0100).</summary>
    private sealed class State
    {
        public string Func = "";
        public string Co = "";
        public string Site = "";
        public string User = "";
        public int BusDate;
        public string Correl = "";
        public int Rc;
        public string Reason = "";
        public string Msg = "";
        public int MsgCnt;
        public bool TranOpen;
        public string Sev = "";
        public string NewRel = "";
        public string HoldText = "";
        public string Field = "";
        public string Text = "";
        public long LineNo;
        public readonly List<LineRow> Lines = [];
        public CredchkArea? Credit;
    }

    public async Task RunAsync(SoacceptArea sa, CancellationToken ct = default)
    {
        var s = new State();
        Initialise(sa, s);
        ValidateInput(sa, s);
        if (s.Rc <= 4)
        {
            switch (s.Func)
            {
                case "VALIDATE":
                    await ValidateOrderAsync(sa, s, ct);
                    break;
                case "ACCEPT":
                    await AcceptOrderAsync(sa, s, ct);
                    break;
                case "RELEASE":
                case "DEPOSIT":
                    throw new NotSupportedException($"SOACCEPT {s.Func} has no production caller (D-SO-01, dispositions).");
                default:
                    s.Rc = 8;
                    s.Reason = "SOA0009";
                    s.Msg = Cob.Str(200, "SOACCEPT does not support function [", Cob.Trim(s.Func), "]");
                    s.MsgCnt++;
                    break;
            }
            if (s.Rc > 4) await RollbackAsync(s, ct);
        }
        SetResult(sa, s);
    }

    /// <summary>0100-INITIALISE.</summary>
    private static void Initialise(SoacceptArea sa, State s)
    {
        var h = sa.Header;
        s.Func = Cob.X(h.Function, 8);
        s.Co = Cob.X(h.Company, 10);
        s.Site = Cob.X(h.Site, 10);
        s.User = Cob.X(h.User, 20);
        s.BusDate = h.BusinessDate;
        s.Correl = Cob.X(h.CorrelationId, 36);
        h.Program = "SOACCEPT";
        h.RequestRef = "SO-003";
        sa.BlockerCnt = 0;
        sa.HardCnt = 0;
        sa.HoldReason = "";
        sa.CreditExposure = 0; sa.CreditLimit = 0;
        sa.CreditHeadroom = 0; sa.CreditExcess = 0;
        sa.OverdueGbp = 0; sa.OverdueDays = 0;
        sa.CreditDecision = "";
        if (Cob.Spaces(sa.DepositCleared) || sa.DepositCleared == "\0") sa.DepositCleared = "N";
        if (Cob.Spaces(sa.PrintOnly) || sa.PrintOnly == "\0") sa.PrintOnly = "N";
    }

    /// <summary>0200-VALIDATE-INPUT.</summary>
    private static void ValidateInput(SoacceptArea sa, State s)
    {
        if (Cob.Spaces(s.Co) || Cob.Spaces(sa.OrderNumber))
        {
            s.Reason = "SOA0001";
            s.Msg = Cob.Str(200, "A company code and an order number are ", "required");
            Reject(s);
        }
    }

    /// <summary>0300-READ-ORDER: the order header overwrites the facade's header fields.</summary>
    private async Task ReadOrderAsync(SoacceptArea sa, State s, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(OrderSql, [SqlioParm.Text(s.Co), SqlioParm.Text(sa.OrderNumber)], OrderWidths, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(s, ex);
            return;
        }
        if (!row.Found)
        {
            s.Rc = 16;
            s.Reason = "SOA0006";
            s.Msg = Cob.Str(200, "Order ", Cob.Trim(sa.OrderNumber), " is not held for company ", Cob.Trim(s.Co));
            s.MsgCnt++;
            return;
        }
        sa.SalesOrderId = (long)Cob.St(row.NumVal(0), 17, 0);
        sa.Customer = Cob.X(row.Raw(1), 20);
        sa.InvoiceCust = Cob.X(row.Raw(2), 20);
        sa.OrderType = Cob.X(row.Raw(3), 12);
        sa.OrderDate = (int)Cob.St(row.NumVal(4), 8, 0, false);
        sa.Currency = Cob.X(row.Raw(5), 3);
        sa.FxRate = Cob.St(row.NumVal(6), 9, 8);
        sa.TermsCode = Cob.X(row.Raw(7), 12);
        sa.Salesperson = Cob.X(row.Raw(8), 20);
        sa.DeliveryRef = Cob.X(row.Raw(9), 10);
        sa.DeliverySnap = Cob.X(row.Raw(10), 300);
        sa.IncotermsRule = Cob.X(row.Raw(11), 3);
        sa.IncotermsPlace = Cob.X(row.Raw(12), 60);
        sa.TcVersion = Cob.X(row.Raw(13), 20);
        sa.RetentionTitle = Cob.X(row.Raw(14), 1);
        sa.VatBasis = Cob.X(row.Raw(15), 20);
        sa.CustVatReg = Cob.X(row.Raw(16), 15);
        sa.VatLocation = Cob.X(row.Raw(17), 3);
        sa.NetAmount = Cob.St(row.NumVal(18), 11, 2);
        sa.VatAmount = Cob.St(row.NumVal(19), 11, 2);
        sa.GrossAmount = Cob.St(row.NumVal(20), 11, 2);
        sa.GrossGbp = Cob.St(row.NumVal(21), 11, 2);
        sa.CommStatus = Cob.X(row.Raw(22), 12);
        sa.PrevCommStatus = Cob.X(row.Raw(22), 12);
        sa.ReleaseStatus = Cob.X(row.Raw(23), 14);
        sa.PrevRelStatus = Cob.X(row.Raw(23), 14);
        sa.FulfilStatus = Cob.X(row.Raw(24), 14);
        sa.BillingStatus = Cob.X(row.Raw(25), 14);
        sa.HoldReason = Cob.X(row.Raw(26), 200);
        sa.DepositReqGbp = Cob.St(row.NumVal(27), 11, 2);
        sa.DepositRcvdGbp = Cob.St(row.NumVal(28), 11, 2);
        sa.LineCount = (int)Cob.St(row.NumVal(30), 9, 0);
        if (!Cob.Spaces(sa.VatLocation) && sa.VatLocation != "GB") sa.International = "Y";
    }

    /// <summary>0400-READ-ORDER-LINES: the open lines (at most 200) in line order.</summary>
    private async Task ReadOrderLinesAsync(SoacceptArea sa, State s, CancellationToken ct)
    {
        s.Lines.Clear();
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await sql.FetchAllAsync(LinesSql, [SqlioParm.Text(s.Co), SqlioParm.Text(sa.OrderNumber)], LineWidths, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(s, ex);
            return;
        }
        foreach (var r in rows)
        {
            if (s.Lines.Count >= 200) break;
            s.Lines.Add(new LineRow
            {
                Num = (long)Cob.St(r.NumVal(0), 9, 0),
                Item = Cob.X(r.Raw(1), 40),
                Rev = Cob.X(r.Raw(2), 4),
                Uom = Cob.X(r.Raw(3), 10),
                Qty = Cob.St(r.NumVal(4), 13, 6),
                PriceSource = Cob.X(r.Raw(5), 20),
                VatCode = Cob.X(r.Raw(6), 4),
                Type = Cob.X(r.Raw(7), 10),
                Status = Cob.X(r.Raw(8), 10),
            });
        }
    }

    /// <summary>1000-VALIDATE-ORDER: every fault recorded, then the worst severity decides.</summary>
    private async Task ValidateOrderAsync(SoacceptArea sa, State s, CancellationToken ct)
    {
        await ReadOrderAsync(sa, s, ct);
        if (s.Rc > 4) return;
        if (sa.CommStatus is "CANCELLED" or "COMPLETED")
        {
            s.Reason = "SOA0010";
            s.Msg = Cob.Str(200, "Order ", Cob.Trim(sa.OrderNumber), " is ", Cob.Trim(sa.CommStatus),
                " and cannot be validated for acceptance");
            Reject(s);
            return;
        }
        await ReadOrderLinesAsync(sa, s, ct);
        if (s.Rc > 4) return;
        CheckHeader(sa, s);
        await CheckLinesAsync(sa, s, ct);
        ScoreFindings(sa, s);
    }

    /// <summary>1100-CHECK-HEADER: the commercial fields an order must carry before it binds.</summary>
    private static void CheckHeader(SoacceptArea sa, State s)
    {
        s.Reason = "SOA0002";
        s.LineNo = 0;
        if (Cob.Spaces(sa.Customer)) Finding(sa, s, "BLOCKER", "CustomerCode", "The ordering customer is not stated");
        if (Cob.Spaces(sa.InvoiceCust))
        {
            Finding(sa, s, "BLOCKER", "InvoiceCustomerCode",
                "The invoiced party is not stated - name it even when it is the ordering party");
        }
        if (Cob.Spaces(sa.DeliveryRef) && Cob.Spaces(sa.DeliverySnap))
        {
            Finding(sa, s, "BLOCKER", "DeliveryAddress", "A delivery address is required before the order can be accepted");
        }
        if (Cob.Spaces(sa.Currency)) Finding(sa, s, "BLOCKER", "CurrencyCode", "A currency is required");
        if (Cob.Spaces(sa.TermsCode)) Finding(sa, s, "BLOCKER", "TermsCode", "Payment terms are required");
        if (Cob.Spaces(sa.Salesperson)) Finding(sa, s, "BLOCKER", "SalespersonCode", "A salesperson is required");
        if (sa.OrderDate == 0) Finding(sa, s, "BLOCKER", "OrderDate", "An order date is required");
        if (Cob.Spaces(sa.TcVersion))
        {
            Finding(sa, s, "BLOCKER", "TermsAndConditionsVersion",
                "The terms and conditions version, including retention of title, must be recorded");
        }
        if (Cob.Spaces(sa.VatBasis)) Finding(sa, s, "BLOCKER", "VatTreatmentBasis", "The basis of the VAT treatment must be stated");
        if (sa.International == "Y" && (Cob.Spaces(sa.IncotermsRule) || Cob.Spaces(sa.IncotermsPlace)))
        {
            s.Reason = "SOA0011";
            Finding(sa, s, "BLOCKER", "IncotermsRule", "An international sale needs an Incoterms rule and a named place");
        }
        if (sa.LineCount == 0)
        {
            s.Reason = "SOA0007";
            Finding(sa, s, "BLOCKER", "SalesOrderLine", "The order has no open lines - there is no demand to accept");
        }
    }

    /// <summary>1200-CHECK-LINES / 1250-CHECK-ONE-LINE (TEXT lines are skipped).</summary>
    private async Task CheckLinesAsync(SoacceptArea sa, State s, CancellationToken ct)
    {
        foreach (var l in s.Lines)
        {
            if (l.Type == "TEXT") continue;
            s.LineNo = l.Num;
            if (Cob.Spaces(l.PriceSource))
            {
                s.Reason = "SOA0004";
                Finding(sa, s, "BLOCKER", "PriceSource", "No price source is recorded - the selected price cannot be explained");
            }
            if (Cob.Spaces(l.VatCode))
            {
                s.Reason = "SOA0004";
                Finding(sa, s, "BLOCKER", "VatCode", "No VAT code is recorded for this line");
            }
            if (Cob.Spaces(l.Item)) continue;
            await ItemEligibilityAsync(sa, s, l, ct);
            await UnitCompatibilityAsync(sa, s, l, ct);
        }
    }

    /// <summary>1300-ITEM-ELIGIBILITY: an obsolete or restricted item is a blocker unless an authority is carried.</summary>
    private async Task ItemEligibilityAsync(SoacceptArea sa, State s, LineRow l, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(ItemSql, [SqlioParm.Text(l.Rev), SqlioParm.Text(s.Co), SqlioParm.Text(l.Item)], ItemWidths, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(s, ex);
            return;
        }
        s.Reason = "SOA0003";
        if (!row.Found)
        {
            Finding(sa, s, "BLOCKER", "ItemCode", Cob.Str(160, "Item ", Cob.Trim(l.Item), " is not in this company's catalogue"));
            return;
        }
        var lifecycle = Cob.X(row.Raw(0), 10);
        var saleable = Cob.X(row.Raw(2), 1);
        var revStatus = Cob.X(row.Raw(3), 10);
        if (lifecycle is "OBSOLETE" or "RETIRED" or "BLOCKED" || saleable == "0")
        {
            if (Cob.Spaces(sa.ItemAuthority))
            {
                Finding(sa, s, "BLOCKER", "ItemCode",
                    Cob.Str(160, "Item ", Cob.Trim(l.Item), " is ", Cob.Trim(lifecycle), " and is not eligible for sale"));
            }
            else
            {
                Finding(sa, s, "WARNING", "ItemCode",
                    Cob.Str(160, "Item ", Cob.Trim(l.Item), " is ", Cob.Trim(lifecycle), " - sold under authority ", Cob.Trim(sa.ItemAuthority)));
            }
        }
        if (revStatus is "SUPERSEDED" or "RETIRED")
        {
            s.Reason = "SOA0003";
            Finding(sa, s, "WARNING", "Revision",
                Cob.Str(160, "Revision ", Cob.Trim(l.Rev), " of ", Cob.Trim(l.Item), " is ", Cob.Trim(revStatus),
                    " - confirm the customer accepts it"));
        }
    }

    /// <summary>1400-UNIT-COMPATIBILITY: UOMCONV TOSTOCK must convert the line unit (SOA0005).</summary>
    private async Task UnitCompatibilityAsync(SoacceptArea sa, State s, LineRow l, CancellationToken ct)
    {
        if (Cob.Spaces(l.Uom))
        {
            s.Reason = "SOA0005";
            Finding(sa, s, "BLOCKER", "UomCode", Cob.Str(160, "No unit of measure is recorded for line item ", Cob.Trim(l.Item)));
            return;
        }
        var uc = new UomconvCommarea
        {
            ItemCode = l.Item,
            FromUom = l.Uom,
            UsageType = "SALES",
            EffDate = CobolDate.FromYyyymmdd(sa.OrderDate),
            EnteredQty = l.Qty,
        };
        var r = await uomconv.ExecuteAsync(UomconvFunctions.ToStock, uc,
            new ItmCall(s.Co, s.Site, s.User, CobolDate.FromYyyymmdd(s.BusDate)), ct);
        if (r.ReturnCode < 8) return;
        s.Reason = "SOA0005";
        Finding(sa, s, "BLOCKER", "UomCode",
            Cob.Str(160, "Unit ", Cob.Trim(l.Uom), " is not compatible with item ", Cob.Trim(l.Item), " - ", Cob.Trim(r.Message)));
    }

    /// <summary>1900-SCORE-THE-FINDINGS: one blocker refuses (RC 08); warnings alone are RC 04.</summary>
    private static void ScoreFindings(SoacceptArea sa, State s)
    {
        if (s.Rc > 4) return;
        if (sa.HardCnt > 0)
        {
            s.Rc = 8;
            s.Msg = Cob.Str(200, "Order ", Cob.Trim(sa.OrderNumber), " is not complete: ", Cob.Ed(sa.HardCnt, "-(9)9"),
                " blocker(s) must be cleared before acceptance");
            s.MsgCnt = sa.BlockerCnt % 100;
            return;
        }
        if (sa.BlockerCnt > 0)
        {
            s.Rc = 4;
            s.MsgCnt = sa.BlockerCnt % 100;
            s.Msg = Cob.Str(200, "Order ", Cob.Trim(sa.OrderNumber), " is complete but carries findings for review");
        }
        else
        {
            s.Msg = Cob.Str(200, "Order ", Cob.Trim(sa.OrderNumber), " is complete and eligible for acceptance");
        }
    }

    /// <summary>2000-ACCEPT-ORDER: validate, credit position, release decision, one unit for the order update, audit.</summary>
    private async Task AcceptOrderAsync(SoacceptArea sa, State s, CancellationToken ct)
    {
        await ValidateOrderAsync(sa, s, ct);
        if (s.Rc > 4) return;
        await CreditPositionAsync(sa, s, ct);
        if (s.Rc > 6) return;
        DecideRelease(sa, s);
        await BeginTranAsync(s, ct);
        await ApplyAcceptanceAsync(sa, s, ct);
        if (s.Rc > 6) return;
        await CommitTranAsync(s, ct);
        sa.CommStatus = "ACCEPTED";
        sa.ReleaseStatus = Cob.X(s.NewRel, 14);
        await AuditTheChangeAsync(sa, s, ct);
        ReportAcceptance(sa, s);
    }

    /// <summary>2100-CREDIT-POSITION: CREDCHK CHECK at ORDER (IC-AR-CREDCHK); RC above 11 refuses (SOA0008, RC 12).</summary>
    private async Task CreditPositionAsync(SoacceptArea sa, State s, CancellationToken ct)
    {
        var ck = new CredchkArea();
        ck.Header.Prepare(new KernelCaller(s.Co, s.Site, s.User, s.BusDate, s.Correl), CreditCheckFunctions.Check);
        ck.Customer = sa.Customer;
        ck.CheckPoint = "ORDER";
        ck.DocumentRef = Cob.X(sa.OrderNumber, 40);
        ck.ExcludeRef = Cob.X(sa.OrderNumber, 40);
        ck.EvalDate = s.BusDate;
        ck.Currency = sa.Currency;
        ck.FxRate = sa.FxRate;
        ck.RateDate = sa.OrderDate;
        ck.OrderValue = sa.GrossAmount;
        ck.OrderValueGbp = sa.GrossGbp;
        ck.OverrideId = sa.OverrideId;
        s.Credit = ck;
        var r = await credchk.CallAsync(ck, ct);
        sa.CreditDecision = Cob.X(ck.Decision, 10);
        sa.CreditExposure = Cob.St(ck.Exposure, 11, 2);
        sa.CreditLimit = Cob.St(ck.Limit, 11, 2);
        sa.CreditHeadroom = Cob.St(ck.Headroom, 11, 2);
        sa.CreditExcess = Cob.St(ck.Excess, 11, 2);
        sa.OverdueGbp = Cob.St(ck.OverdueGbp, 11, 2);
        sa.OverdueDays = ck.OverdueDays;
        sa.OverrideId = ck.OverrideId;
        sa.OverrideExpiry = ck.OverrideExpiry;
        sa.OverrideActive = Cob.X(ck.OverrideActive, 1);
        sa.AccountStatus = Cob.X(ck.AccountStatus, 10);
        if (r.ReturnCode > 11)
        {
            s.Rc = 12;
            s.Reason = "SOA0008";
            s.Msg = Cob.X(r.Message, 200);
            s.MsgCnt++;
            return;
        }
        s.HoldText = Cob.X(ck.DecisionText, 200);
        if (Cob.Spaces(s.HoldText)) s.HoldText = Cob.X(ck.HoldReason, 200);
    }

    /// <summary>2150-DECIDE-RELEASE: D-06 overdue rule, CREDCHK hold/block, commercial findings, outstanding deposit.</summary>
    private static void DecideRelease(SoacceptArea sa, State s)
    {
        s.NewRel = "RELEASED";
        sa.HoldReason = "";
        if (sa.OverdueDays > 30 && (sa.OverrideActive != "Y" || sa.OverrideExpiry < s.BusDate))
        {
            s.NewRel = "CREDITHOLD";
            sa.HoldReason = Cob.Str(200, "Invoices ", Cob.Ed(sa.OverdueDays, "-(9)9"), " days past due totalling ",
                Cob.Ed(sa.OverdueGbp, "Z,ZZZ,ZZZ,ZZ9.99-"), " hold new release (D-06)");
        }
        if (s.Credit is { } ck && (ck.Held || ck.Blocked))
        {
            s.NewRel = "CREDITHOLD";
            if (Cob.Spaces(sa.HoldReason)) sa.HoldReason = s.HoldText;
        }
        if (s.NewRel == "RELEASED" && sa.BlockerCnt > 0)
        {
            s.NewRel = "COMMERCIALHOLD";
            sa.HoldReason = Cob.Str(200, "Commercial findings on the order require ", "review before release");
        }
        if (s.NewRel == "RELEASED" && sa.DepositReqGbp > 0 && sa.DepositRcvdGbp < sa.DepositReqGbp)
        {
            s.NewRel = "COMMERCIALHOLD";
            sa.DepositOutstand = Cob.St(sa.DepositReqGbp - sa.DepositRcvdGbp, 11, 2);
            sa.HoldReason = Cob.Str(200, "Cleared deposit of ", Cob.Ed(sa.DepositOutstand, "Z,ZZZ,ZZZ,ZZ9.99-"),
                " is still outstanding (SO-019)");
        }
        if (s.NewRel != "RELEASED" && s.Rc < 6)
        {
            s.Rc = 6;
            s.Reason = "SOA0008";
        }
    }

    /// <summary>2200-APPLY-ACCEPTANCE: the order update (no row-count test, as legacy).</summary>
    private async Task ApplyAcceptanceAsync(SoacceptArea sa, State s, CancellationToken ct)
    {
        if (s.Rc > 6) return;
        try
        {
            await sql.ExecAsync(AcceptSql,
            [
                SqlioParm.Text(s.NewRel), SqlioParm.Text(sa.HoldReason),
                SqlioParm.Text(Cob.Ed(sa.OverrideId, "-(9)9")), SqlioParm.Text(Cob.Ed(sa.ApprovalId, "-(9)9")),
                SqlioParm.Text(s.User), SqlioParm.Text(s.Co), SqlioParm.Text(sa.OrderNumber),
            ], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(s, ex);
        }
    }

    /// <summary>2300-REPORT-ACCEPTANCE.</summary>
    private static void ReportAcceptance(SoacceptArea sa, State s)
    {
        s.Msg = s.NewRel == "RELEASED"
            ? Cob.Str(200, "Order ", Cob.Trim(sa.OrderNumber), " accepted and released for fulfilment")
            : Cob.Str(200, "Order ", Cob.Trim(sa.OrderNumber), " accepted but held - ", Cob.Trim(sa.HoldReason));
    }

    /// <summary>7500-AUDIT-THE-CHANGE: AUDITLOG WRITE of the release status change (autocommit after the COMT).</summary>
    private async Task AuditTheChangeAsync(SoacceptArea sa, State s, CancellationToken ct)
    {
        if (s.Rc > 6) return;
        var body = new AuditlogArea
        {
            EntityType = "so.SalesOrder",
            EntityKey = Cob.Str(100, Cob.Trim(s.Co), "/", Cob.Trim(sa.OrderNumber)),
            Action = s.Func,
            FieldName = "ReleaseStatus",
            OldValue = sa.PrevRelStatus,
            NewValue = s.NewRel,
            Reason = Cob.Spaces(sa.HoldReason)
                ? Cob.Str(200, "SOACCEPT ", Cob.Trim(s.Func))
                : Cob.Str(200, "SOACCEPT ", Cob.Trim(s.Func), " - ", Cob.Trim(sa.HoldReason)),
            AuthorityRef = sa.OverrideId != 0 ? Cob.Str(40, "CREDIT OVERRIDE ", Cob.Ed(sa.OverrideId, "-(9)9")) : "",
            Source = "SOACCEPT",
            Sensitive = "N",
        };
        await audit.WriteAsync(new KernelCaller(s.Co, s.Site, s.User, s.BusDate, s.Correl), body, ct);
    }

    /// <summary>8000-BEGIN-TRAN.</summary>
    private async Task BeginTranAsync(State s, CancellationToken ct)
    {
        if (s.Rc > 6 || s.TranOpen) return;
        await uow.BeginAsync(ct);
        s.TranOpen = true;
    }

    /// <summary>8100-COMMIT-TRAN.</summary>
    private async Task CommitTranAsync(State s, CancellationToken ct)
    {
        if (!s.TranOpen) return;
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            s.Rc = 12;
            s.Reason = "SQL0001";
            s.Msg = Cob.X(ex.Message, 200);
            s.MsgCnt++;
        }
        s.TranOpen = false;
    }

    /// <summary>9500-ROLLBACK.</summary>
    private async Task RollbackAsync(State s, CancellationToken ct)
    {
        if (!s.TranOpen) return;
        await uow.RollbackAsync(ct);
        s.TranOpen = false;
    }

    private static void Reject(State s)
    {
        s.Rc = 8;
        s.MsgCnt++;
    }

    /// <summary>9300-SQL-FAILED.</summary>
    private static void SqlFailed(State s, SqlioException ex)
    {
        s.Rc = 12;
        s.Reason = "SQL0001";
        s.Msg = Cob.X(ex.DriverText, 200);
        s.MsgCnt++;
    }

    /// <summary>9800-ADD-FINDING: at most 30 findings (later ones are ignored, SME-1127); WS-REASON resets to SOA0002.</summary>
    private static void Finding(SoacceptArea sa, State s, string severity, string field, string text)
    {
        s.Sev = severity;
        s.Field = field;
        s.Text = Cob.X(text, 160);
        if (sa.BlockerCnt >= 30) return;
        sa.BlockerCnt++;
        var b = sa.Blockers[sa.BlockerCnt - 1];
        b.BSeverity = Cob.X(s.Sev, 8);
        b.BReason = Cob.X(s.Reason, 8);
        b.BLineNum = (int)s.LineNo;
        b.BField = Cob.X(s.Field, 40);
        b.BText = s.Text;
        if (s.Sev == "BLOCKER") sa.HardCnt++;
        s.Reason = "SOA0002";
    }

    /// <summary>9900-SET-RESULT.</summary>
    private static void SetResult(SoacceptArea sa, State s)
    {
        var h = sa.Header;
        h.ReturnCode = s.Rc;
        h.ReasonCode = Cob.X(s.Reason, 8);
        h.Message = Cob.X(s.Msg, 200);
        if (s.MsgCnt == 0 && sa.BlockerCnt > 0) s.MsgCnt = sa.BlockerCnt % 100;
        h.MessageCount = s.MsgCnt % 100;
    }
}
