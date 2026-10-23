using System.Globalization;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Domain;

/// <summary>COBOL field, STRING and edit helpers shared by the SO programs.</summary>
public static class Cob
{
    /// <summary>MOVE into PIC X(n): Windows-1252, right truncation; held right-trimmed.</summary>
    public static string X(string? value, int n) => CobolText.Fit(value, n).TrimEnd(' ');

    /// <summary>"= SPACES" on a PIC X(n) field (an empty field is spaces).</summary>
    public static bool Spaces(string? value) => CobolText.IsSpaces((value ?? "").TrimEnd(' '));

    /// <summary>FUNCTION UPPER-CASE (ASCII letters only, GnuCOBOL C locale) into PIC X(n).</summary>
    public static string Upper(string? value, int n) => X(CobolText.Upper(value), n);

    /// <summary>FUNCTION TRIM (leading and trailing spaces).</summary>
    public static string Trim(string? value) => (value ?? "").Trim(' ');

    /// <summary>MOVE SPACES TO target + STRING ... DELIMITED BY SIZE INTO target (PIC X(n)).</summary>
    public static string Str(int n, params string?[] parts) => X(string.Concat(parts), n);

    /// <summary>
    /// STRING ... DELIMITED BY SIZE INTO target without a prior MOVE SPACES: the concatenation overwrites the target from
    /// position 1 and the characters after it keep their previous values.
    /// </summary>
    public static string Overlay(string? current, int n, params string?[] parts)
    {
        var text = CobolText.Fit(string.Concat(parts), n);
        var written = Math.Min(string.Concat(parts).Length, n);
        var old = CobolText.Fit(current, n);
        return (text[..written] + old[written..]).TrimEnd(' ');
    }

    /// <summary>A numeric-edited picture after FUNCTION TRIM.</summary>
    public static string Ed(decimal value, string picture) => CobolEdit.Format(value, picture);

    /// <summary>A numeric-edited picture as the field holds it (untrimmed), for STRING without TRIM.</summary>
    public static string EdField(decimal value, string picture) => CobolEdit.Field(value, picture);

    /// <summary>A PIC 9(n) value as STRING shows it (n digits, leading zeros, low-order digits).</summary>
    public static string Digits(long value, int n)
    {
        var v = Math.Abs(value) % (long)Math.Pow(10, n);
        return v.ToString(new string('0', n), CultureInfo.InvariantCulture);
    }

    /// <summary>A PIC 9(8) date as STRING shows it.</summary>
    public static string D8(int yyyymmdd) => Digits(yyyymmdd, 8);

    /// <summary>MOVE/COMPUTE (no ROUNDED) into PIC [S]9(i)V9(s).</summary>
    public static decimal St(decimal v, int i, int s, bool signed = true) => CobolNumeric.Store(v, i, s, signed);

    /// <summary>COMPUTE ... ROUNDED into PIC [S]9(i)V9(s).</summary>
    public static decimal Rd(decimal v, int i, int s, bool signed = true) => CobolNumeric.Store(v, i, s, signed, rounded: true);

    /// <summary>yyyymmdd of a date.</summary>
    public static int Date8(DateOnly? d) => CobolDate.ToYyyymmdd(d);

    /// <summary>FUNCTION CURRENT-DATE(1:8).</summary>
    public static int Today() => CobolDate.ToYyyymmdd(DateTime.Now);
}

/// <summary>STCAHDR handling shared by the SO programs.</summary>
public static class Header
{
    /// <summary>What CobolGateway.PrepareHeader filled before the LINK (CobolGateway.cs:69-83).</summary>
    public static void Link(Commarea area, SterpContext session, string program, string function, string? correlationId = null)
    {
        var h = area.Header;
        h.Program = program;
        h.Function = CobolText.Upper(function);
        h.Company = Cob.X(session.CompanyCode, 10);
        h.Site = Cob.X(session.SiteCode, 10);
        h.User = Cob.X(session.UserCode, 20);
        h.BusinessDate = CobolDate.ToYyyymmdd(session.BusinessDate);
        h.CorrelationId = Cob.X(string.IsNullOrEmpty(correlationId) ? session.CorrelationId : correlationId, 36);
        h.ReturnCode = 0;
        h.ReasonCode = "";
        h.Message = "";
        h.MessageCount = 0;
    }

    /// <summary>The outcome with CA-REQ-REF as the document reference (contracts 4.6).</summary>
    public static LegacyResult Outcome(this Commarea area)
    {
        var h = area.Header;
        return new LegacyResult(h.ReturnCode, Cob.X(h.ReasonCode, 8), Cob.X(h.Message, 200), h.MessageCount, Cob.X(h.RequestRef, 10));
    }

    /// <summary>The caller values a program moves into a kernel service header (its own CA-COMPANY .. CA-CORRELATION-ID).</summary>
    public static KernelCaller Caller(this Commarea area) =>
        new(area.Header.Company, area.Header.Site, area.Header.User, area.Header.BusinessDate, area.Header.CorrelationId);

    /// <summary>9300-SQL-FAILED of the SO programs: SQLIO-ERRMSG(1:200), or "SQL failure in &lt;program&gt;" when blank.</summary>
    public static string SqlMessage(SqlioException ex, string program)
    {
        var text = CobolText.Fit(ex.DriverText, 512);
        return CobolText.IsSpaces(text) ? "SQL failure in " + program : Cob.X(text, 200);
    }
}
