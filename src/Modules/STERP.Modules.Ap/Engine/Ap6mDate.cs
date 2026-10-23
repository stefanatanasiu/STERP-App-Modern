using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Ap.Engine;

/// <summary>AP6MDTCA (cobol/copy/AP6MDTCA.cpy): the six-month VAT rule area; dates are yyyymmdd numbers.</summary>
public sealed class SixMonthArea
{
    public string Func { get; set; } = "";
    public int Rc { get; set; }
    public string Reason { get; set; } = "";
    public string Message { get; set; } = "";
    public int SupplyDate { get; set; }
    public int DueDate { get; set; }
    public int AsAt { get; set; }
    public int PaidDate { get; set; }
    public int LaterDate { get; set; }
    public int TriggerDate { get; set; }
    public int Months { get; set; }
    public string DueForRepay { get; set; } = "";
    public int DaysOver { get; set; }
    public decimal ClaimedVat { get; set; }
    public decimal Gross { get; set; }
    public decimal Residual { get; set; }
    public decimal AlreadyRepaid { get; set; }
    public decimal RepayVat { get; set; }
    public decimal ReclaimVat { get; set; }
    public decimal UnpaidPct { get; set; }
    public int PeriodDate { get; set; }
    public string PeriodKey { get; set; } = "";
}

/// <summary>
/// AP6MDATE (cobol/online/ap/AP6MDATE.cob:67-274; spec 4.10): the six-month unpaid input VAT date rule and proportions.
/// No database. TRIGGER and PERIOD are entry functions too (deferred callers); REPAY and RECLAIM are reached by APVAT6M.
/// </summary>
public static class Ap6mDate
{
    private static readonly int[] MonthDays = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    public static void Call(SixMonthArea sm)
    {
        sm.Rc = 0;
        sm.Reason = "";
        sm.Message = "";
        switch (sm.Func)
        {
            case "TRIGGER": Trigger(sm); break;
            case "REPAY": Repay(sm); break;
            case "RECLAIM": Reclaim(sm); break;
            case "PERIOD": Period(sm); break;
            default:
                sm.Rc = 8;
                sm.Reason = "AP6M001";
                sm.Message = "Unknown AP6MDATE function";
                break;
        }
    }

    // 1000-TRIGGER: the later of the supply date and the due date plus SM-MONTHS calendar months
    private static void Trigger(SixMonthArea sm)
    {
        if (sm.Months == 0) sm.Months = 6;
        sm.LaterDate = 0;
        if (sm.SupplyDate > sm.LaterDate) sm.LaterDate = sm.SupplyDate;
        if (sm.DueDate > sm.LaterDate) sm.LaterDate = sm.DueDate;
        if (sm.LaterDate == 0)
        {
            sm.Rc = 8;
            sm.Reason = "AP6M002";
            sm.Message = "A supply date or a due date is required";
            sm.TriggerDate = 0;
            sm.DueForRepay = "N";
            return;
        }
        sm.TriggerDate = AddMonths(sm.LaterDate, sm.Months);
        CompareAsAt(sm);
    }

    // 1100-ADD-MONTHS: calendar month shift with the day clamped to the end of the target month
    private static int AddMonths(int date, int months)
    {
        long y = date / 10000 % 10000;
        long m = date / 100 % 100;
        long d = date % 100;
        var totalM = y * 12 + (m - 1) + months;
        y = (long)Math.Floor(totalM / 12m);
        m = totalM - y * 12 + 1;
        if (m > 12)
        {
            m -= 12;
            y += 1;
        }
        var lastDay = LastDay(y, m);
        if (d > lastDay) d = lastDay;
        return (int)(Math.Abs(y * 10000 + m * 100 + d) % 100_000_000);
    }

    // 1150-LAST-DAY
    private static long LastDay(long y, long m)
    {
        long last = m is >= 1 and <= 12 ? MonthDays[m - 1] : 0;
        if (m == 2 && y % 4 == 0 && (y % 100 != 0 || y % 400 == 0)) last = 29;
        return last;
    }

    // 1200-COMPARE-AS-AT: an as-at date on the trigger date itself is due
    private static void CompareAsAt(SixMonthArea sm)
    {
        sm.DueForRepay = "N";
        sm.DaysOver = 0;
        if (sm.AsAt == 0) return;
        var a = CobolDate.IntegerOfDate(sm.AsAt);
        var b = CobolDate.IntegerOfDate(sm.TriggerDate);
        sm.DaysOver = (int)CobolNumeric.Store(a - b, 7, 0);
        if (sm.DaysOver >= 0) sm.DueForRepay = "Y";
        sm.PeriodDate = sm.TriggerDate;
        Period(sm);
    }

    // 2000-REPAY: only the unpaid proportion is repaid, never twice
    private static void Repay(SixMonthArea sm)
    {
        Trigger(sm);
        sm.RepayVat = 0;
        sm.UnpaidPct = 0;
        if (sm.Rc >= 8) return;
        if (sm.DueForRepay != "Y") return;
        if (sm.Residual <= 0 || sm.ClaimedVat <= 0)
        {
            sm.DueForRepay = "N";
            return;
        }
        if (sm.Gross <= 0 || sm.Residual >= sm.Gross)
        {
            sm.RepayVat = sm.ClaimedVat;
            sm.UnpaidPct = 100;
        }
        else
        {
            sm.UnpaidPct = CobolNumeric.Store(sm.Residual * 100 / sm.Gross, 5, 4, rounded: true);
            var raw = CobolNumeric.Store(sm.ClaimedVat * sm.Residual / sm.Gross, 13, 6);
            sm.RepayVat = CobolNumeric.Store(RoundPenny(raw), 13, 2);
        }
        if (sm.AlreadyRepaid != 0)
        {
            sm.RepayVat = CobolNumeric.Store(sm.RepayVat - sm.AlreadyRepaid, 13, 2);
            if (sm.RepayVat <= 0)
            {
                sm.RepayVat = 0;
                sm.DueForRepay = "N";
                sm.Rc = 4;
                sm.Reason = "AP6M003";
                sm.Message = "Input VAT already repaid on this invoice";
            }
        }
    }

    /// <summary>2100-ROUND-PENNY: WS-PENCE = FUNCTION INTEGER(raw x 100 + 0.5) (floor), raw = pence / 100 (SME-903).</summary>
    private static decimal RoundPenny(decimal raw)
    {
        var pence = CobolNumeric.Store(Math.Floor(raw * 100 + 0.5m), 15, 0);
        return CobolNumeric.Store(pence / 100, 13, 6);
    }

    // 3000-RECLAIM: the amount that was repaid, in the return covering the payment date
    private static void Reclaim(SixMonthArea sm)
    {
        sm.ReclaimVat = 0;
        if (sm.AlreadyRepaid <= 0)
        {
            sm.Rc = 4;
            sm.Reason = "AP6M004";
            sm.Message = "Nothing was repaid, so nothing is reclaimable";
            return;
        }
        if (sm.PaidDate == 0)
        {
            sm.Rc = 8;
            sm.Reason = "AP6M005";
            sm.Message = "A payment date is required to reclaim";
            return;
        }
        if (sm.Residual > 0)
        {
            var raw = CobolNumeric.Store(sm.AlreadyRepaid, 13, 6);
            if (sm.Gross > 0) raw = CobolNumeric.Store(sm.AlreadyRepaid * (sm.Gross - sm.Residual) / sm.Gross, 13, 6);
            sm.ReclaimVat = CobolNumeric.Store(RoundPenny(raw), 13, 2);
        }
        else
        {
            sm.ReclaimVat = sm.AlreadyRepaid;
        }
        sm.PeriodDate = sm.PaidDate;
        Period(sm);
    }

    // 4000-PERIOD: calendar quarter key YYYYQn
    private static void Period(SixMonthArea sm)
    {
        sm.PeriodKey = "";
        if (sm.PeriodDate == 0) return;
        var yyyy = sm.PeriodDate / 10000 % 10000;
        var mm = sm.PeriodDate / 100 % 100;
        var qtr = (mm + 2) / 3 % 10;
        sm.PeriodKey = yyyy.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "Q" +
                       qtr.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
