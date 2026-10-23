using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Domain;

/// <summary>
/// PRICING (cobol/online/so/PRICING.cob, FR-PRICING-001..019): deterministic effective-dated selling price selection with
/// the ranked candidate table. SELECT and EXPLAIN build the same candidates; EXPLAIN keeps them (FR-STERP-SO-011).
/// Reads only, autocommit, on the request's engine connection.
/// </summary>
public sealed class PricingProgram(ISqlRunner sql, IParameterService parameters)
{
    private const string Program = "PRICING";

    private const string ListSql =
        "SELECT l.PriceListCode, l.PriceLevel, l.Scope, " +
        "ll.CurrencyCode, ISNULL(ll.UomCode,''), " +
        "ll.QtyBreak, ll.UnitPrice, ll.PricePerQty, " +
        "ll.DiscountPct, ISNULL(ll.MinimumMarginPct,0), " +
        "CONVERT(char(8), ll.ValidFrom, 112), " +
        "ISNULL(CONVERT(char(8), ll.ValidTo, 112),''), " +
        "ISNULL(l.CustomerCode,''), " +
        "ISNULL(l.PriceGroup,''), ISNULL(l.ContractRef,'')" +
        " FROM so.PriceList l JOIN so.PriceListLine ll ON " +
        "ll.CompanyCode = l.CompanyCode AND " +
        "ll.PriceListCode = l.PriceListCode WHERE " +
        "l.CompanyCode = ? AND l.IsActive = 1 AND " +
        "ll.ItemCode = ? AND l.PriceLevel <= ? AND " +
        "l.ValidFrom <= CONVERT(date, ?, 112) AND " +
        "(l.ValidTo IS NULL OR l.ValidTo >= " +
        "CONVERT(date, ?, 112)) AND " +
        "ll.ValidFrom <= CONVERT(date, ?, 112) AND " +
        "(ll.ValidTo IS NULL OR ll.ValidTo >= " +
        "CONVERT(date, ?, 112)) AND (l.Scope = 'STANDARD' " +
        "OR (l.Scope IN ('CONTRACT','CUSTOMER') AND " +
        "l.CustomerCode = ?) OR (l.Scope = 'GROUP' AND " +
        "l.PriceGroup = ?)) ORDER BY l.PriceLevel, " +
        "ll.QtyBreak DESC, l.PriceListCode";

    private static readonly int[] Widths = Enumerable.Repeat(100, 15).ToArray();

    private sealed class State
    {
        public bool InputBad;
        public bool SqlBad;
        public int RejectCur;
        public int RejectUom;
        public int RejectQty;
        public int BestIx;
        public int BestLevel = 99;
        public decimal BestBreak = -1m;
        public decimal BestPrice;
        public decimal BestPer;
        public decimal BestDisc;
    }

    private sealed class Work
    {
        public int Level;
        public string Scope = "";
        public string List = "";
        public string Currency = "";
        public string Uom = "";
        public decimal QtyBrk;
        public decimal Price;
        public decimal Per;
        public decimal Disc;
        public decimal MinMgn;
        public int From;
        public int To;
        public string Why = "";
        public bool Eligible;
    }

    public async Task RunAsync(PricingArea pr, CancellationToken ct = default)
    {
        var s = new State();
        Initialise(pr);
        Validate(pr, s);
        if (s.InputBad) return;
        switch (pr.Header.Function)
        {
            case "SELECT":
            case "EXPLAIN":
                await SelectAsync(pr, s, ct);
                break;
            default:
                pr.Header.ReasonCode = "PRC0008";
                pr.Header.Message = Cob.Str(200, "Function ", Cob.Trim(pr.Header.Function), " is not one of SELECT, EXPLAIN or BREAKS");
                Reject(pr, s);
                break;
        }
    }

    /// <summary>0100-INITIALISE: header reset, keys upper-cased, defaults, outputs cleared.</summary>
    private static void Initialise(PricingArea pr)
    {
        var h = pr.Header;
        h.Program = Program;
        h.ReturnCode = 0;
        h.MessageCount = 0;
        h.ReasonCode = "";
        h.Message = "";
        h.RequestRef = "SO-004";
        h.Function = Cob.Upper(h.Function, 8);
        pr.ItemCode = Cob.Upper(pr.ItemCode, 40);
        pr.Customer = Cob.Upper(pr.Customer, 20);
        pr.Currency = Cob.Upper(pr.Currency, 3);
        pr.UomCode = Cob.Upper(pr.UomCode, 10);
        if (Cob.Spaces(pr.Currency)) pr.Currency = "GBP";
        if (pr.PriceDate == 0) pr.PriceDate = pr.OrderDate;
        if (pr.PriceDate == 0) pr.PriceDate = h.BusinessDate;
        if (pr.MaxLevel < 1 || pr.MaxLevel > 5) pr.MaxLevel = 5;
        pr.UnitPrice = 0; pr.PricePerQty = 0; pr.DiscountPct = 0;
        pr.MinMarginPct = 0; pr.QtyBreak = 0;
        pr.PriceLevel = 0; pr.ValidFrom = 0; pr.ValidTo = 0;
        pr.CandCnt = 0; pr.BreakCnt = 0;
        pr.PriceScope = ""; pr.PriceListCode = "";
        pr.PriceCurrency = ""; pr.PriceUom = "";
        pr.PriceSource = ""; pr.Explanation = "";
        pr.RivalListCode = "";
        pr.PriceFound = "N"; pr.Ambiguous = "N"; pr.BlockConfirm = "N";
        pr.CandTrunc = "N";
    }

    /// <summary>0200-VALIDATE.</summary>
    private static void Validate(PricingArea pr, State s)
    {
        if (Cob.Spaces(pr.Header.Company) || Cob.Spaces(pr.ItemCode))
        {
            pr.Header.ReasonCode = "PRC0001";
            pr.Header.Message = "A company and an item are required";
            Reject(pr, s);
            return;
        }
        if (pr.Header.Function != "BREAKS" && pr.Quantity <= 0)
        {
            pr.Header.ReasonCode = "PRC0002";
            pr.Header.Message = "The quantity must be greater than zero";
            Reject(pr, s);
            return;
        }
        if (pr.ManualPrice != 0 && Cob.Spaces(pr.ManualApproval))
        {
            pr.Header.ReasonCode = "PRC0009";
            pr.Header.Message = Cob.Str(200, "A manual price needs an approval reference ", "before it can take precedence");
            Reject(pr, s);
        }
    }

    /// <summary>1000-SELECT.</summary>
    private async Task SelectAsync(PricingArea pr, State s, CancellationToken ct)
    {
        await ReadXcurrParmAsync(pr, ct);
        AddManual(pr, s);
        await ReadListsAsync(pr, s, ct);
        if (s.SqlBad && pr.CandCnt == 0) return;
        Rank(pr, s);
        Result(pr, s);
        if (pr.Header.Function == "SELECT" && pr.Ambiguous != "Y") pr.CandCnt = 0;
    }

    /// <summary>1100-ADD-MANUAL: an approved manual price is a level-one candidate.</summary>
    private static void AddManual(PricingArea pr, State s)
    {
        if (pr.ManualPrice == 0 || Cob.Spaces(pr.ManualApproval)) return;
        var w = new Work
        {
            Level = 1,
            Scope = "MANUAL",
            List = Cob.X(pr.ManualApproval, 20),
            Currency = pr.Currency,
            Uom = pr.UomCode,
            QtyBrk = 0,
            Price = pr.ManualPrice,
            Per = pr.ManualPerQty > 0 ? pr.ManualPerQty : 1m,
            From = pr.PriceDate,
            To = 0,
            Why = Cob.Str(70, "approved manual price ", Cob.Trim(pr.ManualApproval)),
            Eligible = true,
        };
        AddCandidate(pr, w);
    }

    /// <summary>1200-READ-LISTS + 1250-TEST-ROW: levels two to five from so.PriceList / so.PriceListLine.</summary>
    private async Task ReadListsAsync(PricingArea pr, State s, CancellationToken ct)
    {
        var date = Cob.D8(pr.PriceDate);
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await sql.FetchAllAsync(ListSql,
            [
                SqlioParm.Text(pr.Header.Company), SqlioParm.Text(pr.ItemCode),
                SqlioParm.Text(CobolNumeric.SqlioInteger(pr.MaxLevel).Trim()),
                SqlioParm.Text(date), SqlioParm.Text(date), SqlioParm.Text(date), SqlioParm.Text(date),
                SqlioParm.Text(pr.Customer), SqlioParm.Text(pr.PriceGroup),
            ], Widths, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(pr, s, ex);
            return;
        }
        foreach (var r in rows) TestRow(pr, s, r);
    }

    private static void TestRow(PricingArea pr, State s, SqlioRow r)
    {
        var w = new Work
        {
            List = Cob.X(r.Raw(0), 20),
            Level = (int)Cob.St(r.NumVal(1), 1, 0, false),
            Scope = Cob.X(r.Raw(2), 12),
            Currency = Cob.X(r.Raw(3), 3),
            Uom = Cob.X(r.Raw(4), 10),
            QtyBrk = Cob.St(r.NumVal(5), 13, 6),
            Price = Cob.St(r.NumVal(6), 11, 6),
            Per = Cob.St(r.NumVal(7), 9, 6),
            Disc = Cob.St(r.NumVal(8), 5, 4),
            MinMgn = Cob.St(r.NumVal(9), 5, 4),
            From = (int)Cob.St(r.NumVal(10), 8, 0, false),
            To = Cob.Spaces(r.Raw(11)) ? 0 : (int)Cob.St(r.NumVal(11), 8, 0, false),
        };
        if (w.Per <= 0) w.Per = 1m;
        w.Eligible = true;
        w.Why = "candidate";
        if (w.QtyBrk > pr.Quantity)
        {
            w.Eligible = false;
            s.RejectQty++;
            w.Why = Cob.Str(70, "quantity break ", Cob.Ed(w.QtyBrk, "-(11)9.9(2)"), " is above the order quantity");
        }
        if (w.Eligible && !CobolText.EqualsPadded(w.Currency, pr.Currency))
        {
            if (pr.XcurrAllow == "Y")
            {
                w.Why = Cob.Str(70, "cross currency allowed by ", "SO.PRICE.XCURR.ALLOW");
            }
            else
            {
                w.Eligible = false;
                s.RejectCur++;
                w.Why = Cob.Str(70, "priced in ", Cob.Trim(w.Currency), " not ", Cob.Trim(pr.Currency));
            }
        }
        if (w.Eligible && !Cob.Spaces(w.Uom) && !Cob.Spaces(pr.UomCode) && !CobolText.EqualsPadded(w.Uom, pr.UomCode))
        {
            w.Eligible = false;
            s.RejectUom++;
            w.Why = Cob.Str(70, "priced per ", Cob.Trim(w.Uom), " not ", Cob.Trim(pr.UomCode));
        }
        AddCandidate(pr, w);
    }

    /// <summary>1300-RANK: lowest level, then the largest reached quantity break; disagreeing equals are ambiguous.</summary>
    private static void Rank(PricingArea pr, State s)
    {
        for (var ix = 1; ix <= pr.CandCnt; ix++)
        {
            if (pr.Cand[ix - 1].CChosen != "X") Compare(pr, s, ix);
        }
        if (s.BestIx > 0)
        {
            pr.Cand[s.BestIx - 1].CChosen = "Y";
            pr.PriceFound = "Y";
        }
    }

    /// <summary>1350-COMPARE.</summary>
    private static void Compare(PricingArea pr, State s, int ix)
    {
        var c = pr.Cand[ix - 1];
        if (s.BestIx == 0 || c.CLevel < s.BestLevel)
        {
            TakeBest(pr, s, ix);
            return;
        }
        if (c.CLevel > s.BestLevel)
        {
            c.CWhy = "outranked by level ";
            c.CWhy = Cob.Overlay(c.CWhy, 70, Cob.Trim(c.CWhy), " ", Cob.Ed(s.BestLevel, "-(8)9"));
            return;
        }
        if (c.CQtybrk > s.BestBreak)
        {
            TakeBest(pr, s, ix);
            return;
        }
        if (c.CQtybrk < s.BestBreak)
        {
            c.CWhy = "a larger quantity break applies";
            return;
        }
        if (c.CPrice == s.BestPrice && c.CPerQty == s.BestPer && c.CDisc == s.BestDisc)
        {
            c.CWhy = "identical duplicate of the chosen price";
        }
        else
        {
            pr.Ambiguous = "Y";
            pr.RivalListCode = Cob.X(c.CList, 20);
            c.CWhy = "equally ranked and disagrees on price";
        }
    }

    /// <summary>1360-TAKE-BEST.</summary>
    private static void TakeBest(PricingArea pr, State s, int ix)
    {
        if (s.BestIx > 0)
        {
            var old = pr.Cand[s.BestIx - 1];
            old.CWhy = "outranked by a higher precedence price";
            old.CChosen = "N";
        }
        var c = pr.Cand[ix - 1];
        s.BestIx = ix;
        s.BestLevel = c.CLevel;
        s.BestBreak = c.CQtybrk;
        s.BestPrice = c.CPrice;
        s.BestPer = c.CPerQty;
        s.BestDisc = c.CDisc;
        c.CWhy = "selected - best precedence and quantity band";
    }

    /// <summary>1400-RESULT: the answer and the sentence that explains it (STRING with FUNCTION TRIM of the target).</summary>
    private static void Result(PricingArea pr, State s)
    {
        if (s.BestIx == 0)
        {
            NotFound(pr, s);
            return;
        }
        var c = pr.Cand[s.BestIx - 1];
        pr.UnitPrice = c.CPrice;
        pr.PricePerQty = c.CPerQty;
        pr.DiscountPct = c.CDisc;
        pr.MinMarginPct = c.CMinmgn;
        pr.PriceLevel = c.CLevel;
        pr.PriceScope = Cob.X(c.CScope, 12);
        pr.PriceSource = Cob.X(c.CScope, 10);
        pr.PriceListCode = Cob.X(c.CList, 20);
        pr.PriceCurrency = Cob.X(c.CCurrency, 3);
        pr.PriceUom = Cob.X(c.CUom, 10);
        pr.QtyBreak = c.CQtybrk;
        pr.ValidFrom = c.CFrom;
        pr.ValidTo = c.CTo;
        if (Cob.Spaces(pr.PriceUom)) pr.PriceUom = pr.UomCode;
        var e = "";
        e = Cob.Overlay(e, 200, "Level ");
        e = Cob.Overlay(e, 200, Cob.Trim(e), Cob.Ed(pr.PriceLevel, "-(8)9"), " ", Cob.Trim(pr.PriceScope), " price from list ",
            Cob.Trim(pr.PriceListCode));
        e = Cob.Overlay(e, 200, Cob.Trim(e), ", ", Cob.Trim(pr.PriceCurrency), " ", Cob.Ed(pr.UnitPrice, "-(11)9.9(4)"), " per ");
        e = Cob.Overlay(e, 200, Cob.Trim(e), Cob.Ed(pr.PricePerQty, "-(11)9.9(2)"), " ", Cob.Trim(pr.PriceUom));
        e = Cob.Overlay(e, 200, Cob.Trim(e), ", quantity break ", Cob.Ed(pr.QtyBreak, "-(11)9.9(2)"));
        e = Cob.Overlay(e, 200, Cob.Trim(e), ", effective on ", Cob.D8(pr.PriceDate));
        pr.Explanation = e;
        if (pr.Ambiguous == "Y")
        {
            Ambiguous(pr);
        }
        else
        {
            pr.Header.Message = Cob.X(pr.Explanation, 200);
        }
    }

    /// <summary>1450-NOT-FOUND: RC 16 with the most specific rejection reason.</summary>
    private static void NotFound(PricingArea pr, State s)
    {
        var h = pr.Header;
        pr.PriceFound = "N";
        h.ReturnCode = 16;
        h.MessageCount++;
        if (s.RejectCur > 0)
        {
            h.ReasonCode = "PRC0004";
            h.Message = Cob.Str(200, "No effective price for item ", Cob.Trim(pr.ItemCode), " in ", Cob.Trim(pr.Currency),
                " - prices are not converted between lists");
        }
        else if (s.RejectUom > 0)
        {
            h.ReasonCode = "PRC0005";
            h.Message = Cob.Str(200, "No effective price for item ", Cob.Trim(pr.ItemCode), " per ", Cob.Trim(pr.UomCode));
        }
        else if (s.RejectQty > 0)
        {
            h.ReasonCode = "PRC0006";
            h.Message = Cob.Str(200, "Quantity ", Cob.Ed(pr.Quantity, "-(11)9.9(2)"), " is below the lowest quantity break held ",
                "for item ", Cob.Trim(pr.ItemCode));
        }
        else
        {
            h.ReasonCode = "PRC0003";
            h.Message = Cob.Str(200, "No effective price for item ", Cob.Trim(pr.ItemCode), " on ", Cob.D8(pr.PriceDate));
        }
        pr.Explanation = h.Message;
    }

    /// <summary>1460-AMBIGUOUS: an equal-priority overlap blocks confirmation (RC 08 PRC0007, price kept).</summary>
    private static void Ambiguous(PricingArea pr)
    {
        var h = pr.Header;
        pr.BlockConfirm = "Y";
        h.ReasonCode = "PRC0007";
        h.ReturnCode = 8;
        h.MessageCount++;
        h.Message = Cob.Str(200, "Price lists ", Cob.Trim(pr.PriceListCode), " and ", Cob.Trim(pr.RivalListCode),
            " are equally ranked for item ", Cob.Trim(pr.ItemCode), " and disagree - confirmation is blocked");
        pr.Explanation = h.Message;
    }

    /// <summary>1500-ADD-CANDIDATE: at most 40, then PR-CAND-TRUNC.</summary>
    private static void AddCandidate(PricingArea pr, Work w)
    {
        if (pr.CandCnt >= 40)
        {
            pr.CandTrunc = "Y";
            return;
        }
        pr.CandCnt++;
        var c = pr.Cand[pr.CandCnt - 1];
        c.CLevel = w.Level;
        c.CScope = w.Scope;
        c.CList = w.List;
        c.CCurrency = w.Currency;
        c.CUom = w.Uom;
        c.CQtybrk = w.QtyBrk;
        c.CPrice = w.Price;
        c.CPerQty = w.Per;
        c.CDisc = w.Disc;
        c.CMinmgn = w.MinMgn;
        c.CFrom = w.From;
        c.CTo = w.To;
        c.CRank = pr.CandCnt;
        c.CWhy = Cob.X(w.Why, 70);
        c.CChosen = w.Eligible ? "N" : "X";
    }

    /// <summary>3000-READ-XCURR-PARM: SO.PRICE.XCURR.ALLOW through PARMGET (default N) unless the caller set Y/N.</summary>
    private async Task ReadXcurrParmAsync(PricingArea pr, CancellationToken ct)
    {
        if (pr.XcurrAllow is "Y" or "N") return;
        var caller = new KernelCaller(pr.Header.Company, "", pr.Header.User, pr.Header.BusinessDate, "");
        var pm = await parameters.GetAsync(caller, "SO.PRICE.XCURR.ALLOW", pr.PriceDate, "N", ct: ct);
        pr.XcurrAllow = pm.Header.ReturnCode < 8 && pm.ValueText.StartsWith('Y') ? "Y" : "N";
    }

    private static void Reject(PricingArea pr, State s)
    {
        pr.Header.ReturnCode = 8;
        s.InputBad = true;
        pr.Header.MessageCount++;
    }

    /// <summary>9300-SQL-FAILED.</summary>
    private static void SqlFailed(PricingArea pr, State s, SqlioException ex)
    {
        s.InputBad = true;
        s.SqlBad = true;
        pr.Header.Message = Header.SqlMessage(ex, Program);
        pr.Header.ReasonCode = "SQL0001";
        pr.Header.ReturnCode = 12;
        pr.Header.MessageCount++;
    }
}
