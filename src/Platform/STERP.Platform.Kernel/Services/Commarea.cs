using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>
/// The caller's STCAHDR inputs (CA-COMPANY X(10), CA-SITE X(10), CA-USER X(20), CA-BUSINESS-DATE 9(8),
/// CA-CORRELATION-ID X(36)). Legacy callers moved their own COMMAREA values into the service's header before the CALL
/// (for example ARADJUST.cob:794-799), so a slice passes the values its legacy program passed - usually
/// <see cref="From(SterpContext)"/>, sometimes <see cref="Empty"/> (an INITIALIZEd area) with selected fields.
/// </summary>
public sealed record KernelCaller(string Company, string Site, string User, int BusinessDate, string CorrelationId)
{
    /// <summary>An INITIALIZEd header: spaces and zeros.</summary>
    public static KernelCaller Empty { get; } = new("", "", "", 0, "");

    /// <summary>The request or job context: company, site, effective user, business date (yyyymmdd) and correlation id.</summary>
    public static KernelCaller From(SterpContext ctx) =>
        new(ctx.CompanyCode, ctx.SiteCode, ctx.IsResolved ? ctx.UserCode : "", CobolDate.ToYyyymmdd(ctx.BusinessDate), ctx.CorrelationId);
}

/// <summary>
/// The 400-byte STCAHDR header of every COMMAREA (STCAHDR.cpy:12-32). Text fields are held right-trimmed; the services
/// apply the PIC X(n) widths (Windows-1252, truncation, space padding) whenever they read them.
/// </summary>
public sealed class CommareaHeader
{
    /// <summary>CA-PROGRAM X(8) - set by the service.</summary>
    public string Program { get; set; } = "";

    /// <summary>CA-FUNCTION X(8) - compared exactly (case-sensitive) as the COBOL EVALUATE did.</summary>
    public string Function { get; set; } = "";

    public string Company { get; set; } = "";

    public string Site { get; set; } = "";

    public string User { get; set; } = "";

    /// <summary>CA-BUSINESS-DATE 9(8) yyyymmdd (0 when the caller left it unset).</summary>
    public int BusinessDate { get; set; }

    public string CorrelationId { get; set; } = "";

    /// <summary>CA-RETURN-CODE 9(2): 00 OK, 04 WARNING, 06 HOLD, 08 ERROR, 12 SEVERE, 16 NOT FOUND.</summary>
    public int ReturnCode { get; set; }

    /// <summary>CA-REASON-CODE X(8).</summary>
    public string ReasonCode { get; set; } = "";

    /// <summary>CA-MESSAGE X(200).</summary>
    public string Message { get; set; } = "";

    /// <summary>CA-REQ-REF X(10) - the requirement reference the program stamps (COM-002, COM-009 ...).</summary>
    public string RequestRef { get; set; } = "";

    /// <summary>CA-MSG-COUNT 9(2).</summary>
    public int MessageCount { get; set; }

    /// <summary>The outcome as the kernel/API outcome model carries it (contracts 4.6).</summary>
    public LegacyResult Result => new(ReturnCode, ReasonCode.TrimEnd(), Message.TrimEnd(), MessageCount);

    /// <summary>CA-FAILED: 08 thru 16.</summary>
    public bool Failed => ReturnCode is >= 8 and <= 16;

    /// <summary>Fills the caller fields and the function (what a legacy caller did before CALL).</summary>
    public CommareaHeader Prepare(KernelCaller caller, string function)
    {
        Function = function;
        Company = caller.Company;
        Site = caller.Site;
        User = caller.User;
        BusinessDate = caller.BusinessDate;
        CorrelationId = caller.CorrelationId;
        ReturnCode = 0;
        ReasonCode = "";
        Message = "";
        MessageCount = 0;
        return this;
    }

    /// <summary>The 0100-INITIALISE header reset every common service performs.</summary>
    internal void Reset(string program, string requestRef)
    {
        Program = program;
        ReturnCode = 0;
        ReasonCode = "";
        Message = "";
        RequestRef = requestRef;
        MessageCount = 0;
    }

    /// <summary>9000-REJECT: RC 08 and one more message.</summary>
    internal void Reject(string reason, string message)
    {
        ReasonCode = reason;
        Message = K.Msg(message);
        ReturnCode = 8;
        MessageCount++;
    }

    internal string FunctionCode => K.X(Function, 8).TrimEnd(' ');
}

/// <summary>A COMMAREA: the STCAHDR header followed by the program's body fields.</summary>
public abstract class Commarea
{
    public CommareaHeader Header { get; } = new();

    public LegacyResult Result => Header.Result;
}

/// <summary>Legacy field and message helpers shared by the kernel services.</summary>
internal static class K
{
    /// <summary>The value as PIC X(n) holds it.</summary>
    public static string X(string? value, int n) => CobolText.Fit(value, n);

    /// <summary>The value as the area stores it after a MOVE into PIC X(n): fitted and right-trimmed.</summary>
    public static string Store(string? value, int n) => CobolText.Fit(value, n).TrimEnd(' ');

    /// <summary>"= SPACES OR = LOW-VALUES" on PIC X(n).</summary>
    public static bool Blank(string? value, int n) => CobolText.IsBlank(value, n);

    /// <summary>"= SPACES" on PIC X(n) (LOW-VALUES not included).</summary>
    public static bool Spaces(string? value, int n) => CobolText.IsSpaces(CobolText.Fit(value, n));

    /// <summary>FUNCTION TRIM (leading and trailing spaces; an all-space field gives the empty string).</summary>
    public static string Trim(string? value) => (value ?? "").Trim(' ');

    /// <summary>STRING ... DELIMITED BY SIZE INTO CA-MESSAGE after MOVE SPACES: concatenation cut at 200 bytes.</summary>
    public static string Msg(params string?[] parts) => Store(string.Concat(parts), 200);

    /// <summary>Text the edited numeric field shows after FUNCTION TRIM.</summary>
    public static string Ed(decimal value, string picture) => CobolEdit.Format(value, picture);

    /// <summary>9300-SQL-FAILED: SQLIO-ERRMSG(1:200), or "SQL failure in &lt;program&gt;" when the driver text is blank.</summary>
    public static string SqlFailureMessage(SqlioException ex, string program)
    {
        var full = CobolText.Fit(ex.DriverText, 512);
        return CobolText.IsSpaces(full) ? "SQL failure in " + program : Store(full, 200);
    }

    /// <summary>The business-refusal test of the common services: CA-MESSAGE(1:8) = "STERP-E-" or "STERP-W-".</summary>
    public static bool IsBusinessText(string message) =>
        message.StartsWith("STERP-E-", StringComparison.Ordinal) || message.StartsWith("STERP-W-", StringComparison.Ordinal);

    /// <summary>The 9300-SQL-FAILED pattern of NUMGEN, IDEMPOT, AUDITLOG, EXCPRAIS, PARMGET and JOBLOG.</summary>
    public static void SqlFailed(CommareaHeader h, SqlioException ex, string program, string businessReason)
    {
        h.Message = SqlFailureMessage(ex, program);
        if (IsBusinessText(h.Message))
        {
            h.ReasonCode = businessReason;
            h.ReturnCode = 8;
        }
        else
        {
            h.ReasonCode = "SQL0001";
            h.ReturnCode = 12;
        }
        h.MessageCount++;
    }

    /// <summary>A PIC 9(8) date field as STRING shows it (eight digits, leading zeros).</summary>
    public static string D8(int yyyymmdd) => (yyyymmdd < 0 ? 0 : yyyymmdd % 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
}
