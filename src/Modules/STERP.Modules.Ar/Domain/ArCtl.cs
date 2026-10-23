using System.Globalization;
using Sterp.Modules.Ar.Contracts;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Domain;

/// <summary>
/// AR-CTL of ARWORK.cpy (cobol/copy/ARWORK.cpy:16-41): the standard header values an AR program works on while it runs
/// (its body uses AR-RC / AR-REASON / AR-MSG and one section copies them back to the COMMAREA at the end).
/// </summary>
public sealed class ArCtl
{
    private string _reason = "";
    private string _msg = "";

    /// <summary>AR-RC 9(2).</summary>
    public int Rc { get; set; }

    /// <summary>AR-REASON X(8).</summary>
    public string Reason
    {
        get => _reason;
        set => _reason = CobolText.Trimmed(value, 8);
    }

    /// <summary>AR-MSG X(200) (a MOVE/STRING into it keeps the first 200 characters).</summary>
    public string Msg
    {
        get => _msg;
        set => _msg = CobolText.Trimmed(value, 200);
    }

    /// <summary>AR-MSG-CNT 9(2).</summary>
    public int MsgCnt { get; set; }

    public string Func { get; set; } = "";
    public string Co { get; set; } = "";
    public string Site { get; set; } = "";
    public string UserId { get; set; } = "";
    public int BusDate { get; set; }
    public string Correl { get; set; } = "";
    public string ReqRef { get; set; } = "";

    /// <summary>AR-ERROR = "Y" (88 AR-INPUT-BAD).</summary>
    public bool InputBad { get; set; }

    /// <summary>AR-IN-TRANSACTION.</summary>
    public bool InTransaction { get; set; }

    public bool RcOk => Rc == 0;
    public bool RcWarn => Rc == 4;
    public bool RcHold => Rc == 6;
    public bool RcError => Rc == 8;
    public bool RcSevere => Rc == 12;
    public bool RcNotFound => Rc == 16;

    /// <summary>88 AR-RC-BAD 08 THRU 16.</summary>
    public bool RcBad => Rc is >= 8 and <= 16;

    /// <summary>88 AR-RC-STOP 06 THRU 16 (a hold is as final as an error for anything that would otherwise post).</summary>
    public bool RcStop => Rc is >= 6 and <= 16;

    /// <summary>0100-INITIALISE: the header MOVEs into AR-CTL, CA-PROGRAM set, result cleared.</summary>
    public static ArCtl Start(CommareaHeader h, string program, string reqRef)
    {
        h.Program = program;
        return new ArCtl
        {
            Func = CobolText.Trimmed(h.Function, 8),
            Co = CobolText.Trimmed(h.Company, 10),
            Site = CobolText.Trimmed(h.Site, 10),
            UserId = CobolText.Trimmed(h.User, 20),
            BusDate = h.BusinessDate,
            Correl = CobolText.Trimmed(h.CorrelationId, 36),
            ReqRef = reqRef,
        };
    }

    /// <summary>9000-REJECT: RC 08, AR-ERROR "Y", one more message.</summary>
    public void Reject(string reason, string message)
    {
        Reason = reason;
        Msg = message;
        Rc = 8;
        InputBad = true;
        MsgCnt++;
    }

    /// <summary>9300-SQL-FAILED: RC 12 SQL0001 with SQLIO-ERRMSG (first 200 characters), one more message.</summary>
    public void SqlFailed(string driverText)
    {
        Rc = 12;
        Reason = "SQL0001";
        Msg = driverText;
        MsgCnt++;
    }

    /// <summary>9900-SET-RESULT: the one place that writes the standard header back.</summary>
    public void SetResult(CommareaHeader h)
    {
        h.ReturnCode = Rc;
        h.ReasonCode = Reason;
        h.Message = Msg;
        h.MessageCount = MsgCnt % 100;
        h.RequestRef = CobolText.Trimmed(ReqRef, 10);
    }
}

/// <summary>The ARWORK edit masks and conversions used to build SQL parameters and messages.</summary>
public static class ArEdit
{
    /// <summary>MOVE x TO AR-MONEY-ED (PIC -(13)9.99), FUNCTION TRIM.</summary>
    public static string Money(decimal value) => Edited(value, 2, false);

    /// <summary>MOVE x TO AR-MONEY-UK (PIC Z,ZZZ,ZZZ,ZZ9.99-), FUNCTION TRIM: grouped, trailing minus when negative.</summary>
    public static string MoneyUk(decimal value)
    {
        var v = Math.Abs(CobolNumeric.Truncated(value, 2)) % 10_000_000_000m;
        var text = v.ToString("#,##0.00", CultureInfo.InvariantCulture);
        return value < 0 && v != 0 ? text + "-" : text;
    }

    /// <summary>MOVE x TO AR-INT-ED (PIC -(9)9), FUNCTION TRIM.</summary>
    public static string Int(long value) => (value % 10_000_000_000L).ToString(CultureInfo.InvariantCulture);

    /// <summary>MOVE x TO AR-MONEY-UK used untrimmed: the 17-character edited field (leading spaces, sign position).</summary>
    public static string MoneyUkField(decimal value)
    {
        var v = Math.Abs(CobolNumeric.Truncated(value, 2)) % 10_000_000_000m;
        return v.ToString("#,##0.00", CultureInfo.InvariantCulture).PadLeft(16) + (value < 0 && v != 0 ? "-" : " ");
    }

    /// <summary>MOVE x TO AR-RATE-ED (PIC -(5)9.9(8)), FUNCTION TRIM.</summary>
    public static string Rate(decimal value) => Edited(value, 8, false);

    /// <summary>MOVE x TO AR-QTY-ED (PIC -(10)9.9(6)), FUNCTION TRIM.</summary>
    public static string Qty(decimal value) => Edited(value, 6, false);

    private static string Edited(decimal value, int scale, bool _)
    {
        var t = CobolNumeric.Truncated(value, scale);
        var text = Math.Abs(t).ToString("0." + new string('0', scale), CultureInfo.InvariantCulture);
        return t < 0 ? "-" + text : text;
    }

    /// <summary>A PIC 9(8) date as its eight digits (MOVE date TO X field).</summary>
    public static string D8(int yyyymmdd) => (Math.Abs(yyyymmdd) % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>dd/mm/yyyy from a PIC 9(8) date: date(7:2) "/" date(5:2) "/" date(1:4).</summary>
    public static string Dmy(int yyyymmdd)
    {
        var d = D8(yyyymmdd);
        return d[6..8] + "/" + d[4..6] + "/" + d[..4];
    }

    /// <summary>"IF text(1:8) IS NUMERIC MOVE text(1:8) TO date-field".</summary>
    public static bool TryDate8(string? text, out int value)
    {
        value = 0;
        var t = CobolText.Fit(text, 8);
        if (!t.All(char.IsAsciiDigit)) return false;
        value = int.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>The current date as FUNCTION CURRENT-DATE(1:8).</summary>
    public static int Today() => CobolDate.ToYyyymmdd(DateTime.Now);
}
