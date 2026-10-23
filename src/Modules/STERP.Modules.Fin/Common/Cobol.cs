using System.Globalization;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Common;

/// <summary>
/// The STCAHDR result header as a COBOL program maintains it while it runs (cobol/copy/STCAHDR.cpy): return code, reason,
/// message (X(200)), message count and CA-REQ-REF. Programs set the fields step by step exactly as the legacy paragraphs do.
/// </summary>
public sealed class CaHeader
{
    public CaHeader(string reqRef = "") => ReqRef = reqRef;

    public int ReturnCode { get; set; }

    public string ReasonCode { get; set; } = "";

    private string _message = "";

    /// <summary>CA-MESSAGE PIC X(200): right-truncated when moved.</summary>
    public string Message
    {
        get => _message;
        set => _message = CobolText.Trimmed(value, 200);
    }

    public int MsgCount { get; set; }

    public string ReqRef { get; set; }

    /// <summary>CA-FAILED: 08 thru 16.</summary>
    public bool Failed => ReturnCode is >= 8 and <= 16;

    /// <summary>The common 9000-REJECT paragraph: RC 08 and one more message.</summary>
    public void Reject(string reason, string message)
    {
        ReasonCode = reason;
        Message = message;
        ReturnCode = 8;
        MsgCount++;
    }

    /// <summary>Reason, message and return code set together with one more message (e.g. RC 16 not found).</summary>
    public void Set(int rc, string reason, string message)
    {
        ReasonCode = reason;
        Message = message;
        ReturnCode = rc;
        MsgCount++;
    }

    /// <summary>The common 9300-SQL-FAILED paragraph: RC 12 SQL0001 with SQLIO-ERRMSG(1:200).</summary>
    public void SqlFailed(SqlioException ex, string program)
    {
        ReasonCode = "SQL0001";
        var text = CobolText.Trimmed(ex.DriverText, 200);
        Message = text.Length == 0 ? "SQL failure in " + program : text;
        ReturnCode = 12;
        MsgCount++;
    }

    public void CopyFrom(LegacyResult r)
    {
        ReturnCode = r.ReturnCode;
        ReasonCode = r.ReasonCode;
        Message = r.Message;
        MsgCount = r.MessageCount;
    }

    public LegacyResult ToResult() => new(ReturnCode, ReasonCode, Message, MsgCount % 100, ReqRef);
}

/// <summary>Edited numeric pictures used in FIN messages (kernel edit semantics).</summary>
public static class FinEdit
{
    /// <summary>MOVE to PIC -(11)9.99 then FUNCTION TRIM (no thousands separator, floating minus).</summary>
    public static string Money(decimal value) => Sterp.Platform.Kernel.Cobol.CobolEdit.Format(value, "-(11)9.99");

    /// <summary>The untrimmed PIC -(11)9.99 field (15 characters) as moved to another alphanumeric field.</summary>
    public static string MoneyField(decimal value) => Sterp.Platform.Kernel.Cobol.CobolEdit.Field(value, "-(11)9.99");
}
/// <summary>Text helpers mirroring the COBOL field moves of rows fetched through SQLIO.</summary>
public static class Slot
{
    /// <summary>The fetched column as it lands in a PIC X(width) slot (NULL -> spaces), right-trimmed.</summary>
    public static string Text(object? value, int width) => CobolText.Slot(value, width).TrimEnd();

    /// <summary>FUNCTION NUMVAL of a fetched numeric column (NULL/blank -> 0).</summary>
    public static decimal Num(object? value)
    {
        return value switch
        {
            null or DBNull => 0m,
            decimal d => d,
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            bool bo => bo ? 1 : 0,
            double db => (decimal)db,
            string s => NumVal(s),
            _ => 0m,
        };
    }

    /// <summary>
    /// FUNCTION NUMVAL as the deployed GnuCOBOL 3.2 runtime evaluates it (kernel, SME-039). The legacy BANKREC member check
    /// depends on its leniency ("1" followed by the next slot's "0" reads as 10, TBANKREC 30).
    /// </summary>
    public static decimal NumVal(string? text) => CobolNumeric.NumVal(text);

    /// <summary>NUMVAL of a date column fetched as ODBC text ('yyyy-mm-dd') into a slot of the given width.</summary>
    public static decimal NumValOfDate(object? value, int width)
    {
        if (value is not DateTime dt) return NumVal(value as string);
        var text = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return NumVal(text.Length > width ? text[..width] : text);
    }

    public static long Int(object? value) => (long)decimal.Truncate(Num(value));

    /// <summary>A date column fetched as CONVERT(char(8), x, 112) into a 9(8) field (NULL -> 0).</summary>
    public static int Ymd(object? value) => value switch
    {
        null or DBNull => 0,
        DateTime dt => CobolDate.ToYyyymmdd(dt),
        string s when int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) => v,
        _ => 0,
    };
}
