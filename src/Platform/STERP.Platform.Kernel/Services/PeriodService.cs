using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>PERIODCKCA body (PERIODCKCA.cpy:21-34).</summary>
public sealed class PeriodckArea : Commarea
{
    /// <summary>PC-POSTING-DATE 9(8); 0 takes CA-BUSINESS-DATE (no clock fallback).</summary>
    public int PostingDate { get; set; }

    /// <summary>PC-DOCUMENT-DATE 9(8) (validated when non-zero, otherwise unused).</summary>
    public int DocumentDate { get; set; }

    /// <summary>PC-TAX-POINT 9(8) (validated when non-zero, otherwise unused).</summary>
    public int TaxPoint { get; set; }

    /// <summary>PC-ACTION X(12): defaults to POST, upper-cased, only echoed.</summary>
    public string Action { get; set; } = "";

    /// <summary>PC-FISCAL-YEAR S9(4).</summary>
    public int FiscalYear { get; set; }

    /// <summary>PC-PERIOD-NO S9(4).</summary>
    public int PeriodNo { get; set; }

    /// <summary>PC-PERIOD-YYYYPP X(6).</summary>
    public string PeriodKey { get; set; } = "";

    /// <summary>PC-PERIOD-STATUS X(10): OPEN, SOFT, HARD, FUTURE, other, or CLOSED/SOFT after a refused procedure call.</summary>
    public string PeriodStatus { get; set; } = "";

    /// <summary>PC-ALLOWED X.</summary>
    public string Allowed { get; set; } = "";

    /// <summary>PC-OVERRIDE-PERM X(40): FIN.PERIOD.SOFTPOST on every answer.</summary>
    public string OverridePermission { get; set; } = "";

    /// <summary>PC-REASON-TEXT X(200).</summary>
    public string ReasonText { get; set; } = "";

    public bool IsAllowed => Allowed == "Y";
}

/// <summary>PERIODCK - posting date and period policy (FR-STERP-PLATFORM-013, COM-009).</summary>
public interface IPeriodService
{
    /// <summary>CALL "PERIODCK" USING area: functions CHECK and DATES (identical); other PER0009 after date validation.</summary>
    Task<LegacyResult> ExecuteAsync(PeriodckArea area, CancellationToken ct = default);

    /// <summary>Function CHECK (or DATES): RC 00 allowed, 06 PER0006 soft closed without authority, 08 refused, 12 SQL failure.</summary>
    Task<PeriodckArea> CheckAsync(KernelCaller caller, int postingDate, int documentDate = 0, int taxPoint = 0, string? action = null,
        string function = "CHECK", CancellationToken ct = default);
}

/// <summary>PERIODCK.cob:57-334: the inline fin.Period read on SQLIO slot 6 (fin.usp_ValidatePeriod is not used).</summary>
public sealed class PeriodService(ISqlRunner sql) : IPeriodService
{
    public const string SoftPostPermission = "FIN.PERIOD.SOFTPOST";

    public async Task<PeriodckArea> CheckAsync(KernelCaller caller, int postingDate, int documentDate = 0, int taxPoint = 0, string? action = null,
        string function = "CHECK", CancellationToken ct = default)
    {
        var area = new PeriodckArea { PostingDate = postingDate, DocumentDate = documentDate, TaxPoint = taxPoint, Action = action ?? "" };
        area.Header.Prepare(caller, function);
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<LegacyResult> ExecuteAsync(PeriodckArea a, CancellationToken ct = default)
    {
        var h = a.Header;
        // 0100-INITIALISE
        h.Reset("PERIODCK", "COM-009");
        a.FiscalYear = 0;
        a.PeriodNo = 0;
        a.PeriodStatus = "";
        a.PeriodKey = "";
        a.ReasonText = "";
        a.Allowed = "N";
        a.OverridePermission = SoftPostPermission;
        var action = K.X(a.Action, 12);
        if (K.Blank(action, 12)) action = K.X("POST", 12);
        action = CobolText.Upper(action);
        a.Action = action.TrimEnd(' ');
        if (a.PostingDate == 0) a.PostingDate = h.BusinessDate;

        // 0200-VALIDATE-DATES
        if (K.Blank(h.Company, 10))
        {
            Reject(a, "PER0001", "A company code is required");
            return h.Result;
        }
        if (CobolDate.TestDate(a.PostingDate) != 0)
        {
            Reject(a, "PER0002", "The posting date is not a valid calendar date");
            return h.Result;
        }
        if (a.DocumentDate != 0 && CobolDate.TestDate(a.DocumentDate) != 0)
        {
            Reject(a, "PER0003", "The document date is not a valid calendar date");
            return h.Result;
        }
        if (a.TaxPoint != 0 && CobolDate.TestDate(a.TaxPoint) != 0)
        {
            Reject(a, "PER0004", "The tax point is not a valid calendar date");
            return h.Result;
        }

        var fn = h.FunctionCode;
        if (fn is not ("CHECK" or "DATES"))
        {
            Reject(a, "PER0009", "PERIODCK does not support function " + K.Trim(h.Function));
            return h.Result;
        }

        // 1000-CHECK-PERIOD
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT TOP (1) p.CompanyCode, CAST(p.FiscalYear AS varchar(8)), CAST(p.PeriodNo AS varchar(8)), p.Status, " +
                "CAST(core.fn_HasPermission(?, 'FIN.PERIOD.SOFTPOST', p.CompanyCode, NULL) AS varchar(4)), ? FROM fin.Period p " +
                "WHERE p.CompanyCode = ? AND ? BETWEEN p.StartDate AND p.EndDate",
                [K.Spaces(h.User, 20) ? SqlioParm.Null : SqlioParm.Text(K.X(h.User, 20)), SqlioParm.Text(action),
                 SqlioParm.Text(K.X(h.Company, 10)), SqlioParm.Digits(a.PostingDate, 8)],
                [10, 8, 8, 10, 4, 12], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(a, ex);
            return h.Result;
        }
        if (!row.Found)
        {
            Reject(a, "PER0005", "No financial period covers that posting date");
            a.ReasonText = h.Message;
            return h.Result;
        }
        var fy = (long)CobolNumeric.Store(row.NumVal(1), 9, 0);
        var pn = (long)CobolNumeric.Store(row.NumVal(2), 9, 0);
        a.FiscalYear = (int)(fy % 10000);
        a.PeriodNo = (int)(pn % 10000);
        a.PeriodKey = (Math.Abs(fy) % 10000).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) +
                      (Math.Abs(pn) % 100).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        a.PeriodStatus = row.Text(3);
        ApplyPolicy(a, row.Raw(4));
        return h.Result;
    }

    /// <summary>1100-APPLY-PERIOD-POLICY.</summary>
    private static void ApplyPolicy(PeriodckArea a, string allowedSlot)
    {
        var h = a.Header;
        var date = K.D8(a.PostingDate);
        var key = K.X(a.PeriodKey, 6);
        h.Message = "";
        switch (K.X(a.PeriodStatus, 10).TrimEnd(' '))
        {
            case "OPEN":
                a.Allowed = "Y";
                h.ReturnCode = 0;
                h.Message = K.Msg("Posting date ", date, " is in period ", key, " (OPEN)");
                break;
            case "SOFT":
                if (allowedSlot == "1   ")
                {
                    a.Allowed = "Y";
                    h.ReturnCode = 0;
                    h.Message = K.Msg("Posting date ", date, " is in period ", key, " (SOFT) - posted as a close adjustment");
                }
                else
                {
                    a.Allowed = "N";
                    h.ReturnCode = 6;
                    h.ReasonCode = "PER0006";
                    a.OverridePermission = SoftPostPermission;
                    h.MessageCount++;
                    h.Message = K.Msg("Period ", key, " is soft closed - only an authorised close adjustment may be posted into it");
                }
                break;
            case "HARD":
                a.Allowed = "N";
                h.ReasonCode = "PER0003";
                h.Message = K.Msg("Period ", key, " is hard closed - post the correction in the current open period or ask the financial controller to reopen it");
                Reject(a);
                break;
            case "FUTURE":
                a.Allowed = "N";
                h.ReasonCode = "PER0004";
                h.Message = K.Msg("Period ", key, " is not yet open for posting");
                Reject(a);
                break;
            default:
                a.Allowed = "N";
                h.ReasonCode = "PER0007";
                h.Message = K.Msg("Period ", key, " has an unknown status ", K.Trim(a.PeriodStatus));
                Reject(a);
                break;
        }
        a.ReasonText = h.Message;
    }

    /// <summary>9300-SQL-FAILED with 9400-SOFT-CLOSE-TEST (only "STERP-E-" text is a business refusal here).</summary>
    private static void SqlFailed(PeriodckArea a, SqlioException ex)
    {
        var h = a.Header;
        a.Allowed = "N";
        h.Message = K.SqlFailureMessage(ex, "PERIODCK");
        a.ReasonText = h.Message;
        if (!h.Message.StartsWith("STERP-E-", StringComparison.Ordinal))
        {
            h.ReasonCode = "SQL0001";
            h.ReturnCode = 12;
        }
        else
        {
            h.ReasonCode = "PER0006";
            a.PeriodStatus = "CLOSED";
            h.ReturnCode = 8;
            // INSPECT CA-MESSAGE TALLYING CA-MSG-COUNT FOR ALL "soft closed"; the count is then reset to 0
            var tally = CountOccurrences(K.X(h.Message, 200), "soft closed");
            if (h.MessageCount + tally > 0)
            {
                a.PeriodStatus = "SOFT";
                h.ReasonCode = "PER0007";
                h.ReturnCode = 6;
            }
            h.MessageCount = 0;
        }
        h.MessageCount++;
    }

    private static int CountOccurrences(string text, string what)
    {
        var n = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static void Reject(PeriodckArea a, string reason, string message)
    {
        a.Header.ReasonCode = reason;
        a.Header.Message = K.Msg(message);
        Reject(a);
    }

    /// <summary>9000-REJECT.</summary>
    private static void Reject(PeriodckArea a)
    {
        a.Allowed = "N";
        a.Header.ReturnCode = 8;
        a.Header.MessageCount++;
    }
}
