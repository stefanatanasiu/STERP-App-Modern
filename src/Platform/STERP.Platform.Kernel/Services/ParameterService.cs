using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>PARMGETCA body (PARMGETCA.cpy:19-35).</summary>
public sealed class ParmgetArea : Commarea
{
    /// <summary>PM-PARAM-CODE X(40) - upper-cased by the service.</summary>
    public string ParamCode { get; set; } = "";

    /// <summary>PM-AS-OF 9(8): 0 takes CA-BUSINESS-DATE, then the host clock date.</summary>
    public int AsOf { get; set; }

    /// <summary>PM-VALUE-TEXT X(200).</summary>
    public string ValueText { get; set; } = "";

    /// <summary>PM-VALUE-NUM S9(13)V9(6): NUMVAL of the value when it looks numeric.</summary>
    public decimal ValueNum { get; set; }

    /// <summary>PM-DATA-TYPE X(12).</summary>
    public string DataType { get; set; } = "";

    /// <summary>PM-UNIT X(20).</summary>
    public string Unit { get; set; } = "";

    /// <summary>PM-DECISION-REF X(10).</summary>
    public string DecisionRef { get; set; } = "";

    /// <summary>PM-OWNER-ROLE X(20).</summary>
    public string OwnerRole { get; set; } = "";

    /// <summary>PM-DESCRIPTION X(100) (the first 100 bytes of the stored description).</summary>
    public string Description { get; set; } = "";

    /// <summary>PM-EFFECTIVE-FROM 9(8) yyyymmdd.</summary>
    public int EffectiveFrom { get; set; }

    /// <summary>PM-SCOPE X(10): COMPANY, ALL or DEFAULT.</summary>
    public string Scope { get; set; } = "";

    /// <summary>PM-DEFAULT-TEXT X(200): the caller's default (used when PM-USE-DEFAULT is "Y").</summary>
    public string DefaultText { get; set; } = "";

    /// <summary>PM-USE-DEFAULT X.</summary>
    public string UseDefault { get; set; } = "";

    /// <summary>PM-FOUND X.</summary>
    public string Found { get; set; } = "";

    /// <summary>PM-IS-NUMERIC X.</summary>
    public string IsNumeric { get; set; } = "";
}

/// <summary>PARMGET - effective-dated company policy values (FR-STERP-PLATFORM-018, COM-025).</summary>
public interface IParameterService
{
    /// <summary>CALL "PARMGET" USING area: functions GET and GETNUM (other PRM0009 after validation).</summary>
    Task<LegacyResult> ExecuteAsync(ParmgetArea area, CancellationToken ct = default);

    /// <summary>GET (or GETNUM): RC 00 found, 04 PRM0004 default used, 08 invalid/non-numeric, 12 SQL failure, 16 PRM0003 not held.</summary>
    Task<ParmgetArea> GetAsync(KernelCaller caller, string paramCode, int asOf = 0, string? defaultText = null, bool numeric = false, CancellationToken ct = default);
}

/// <summary>
/// PARMGET.cob:61-331: PARMGET's own SELECT on core.Parameter (not core.usp_GetParameter, BRF-PARMGET-01) on SQLIO slot 6.
/// The as-of fallback to the host clock uses the injected <see cref="TimeProvider"/> (local date).
/// </summary>
public sealed class ParameterService(ISqlRunner sql, TimeProvider clock) : IParameterService
{
    public async Task<ParmgetArea> GetAsync(KernelCaller caller, string paramCode, int asOf = 0, string? defaultText = null, bool numeric = false, CancellationToken ct = default)
    {
        var area = new ParmgetArea { ParamCode = paramCode, AsOf = asOf };
        if (defaultText is not null)
        {
            area.UseDefault = "Y";
            area.DefaultText = defaultText;
        }
        area.Header.Prepare(caller, numeric ? "GETNUM" : "GET");
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<LegacyResult> ExecuteAsync(ParmgetArea a, CancellationToken ct = default)
    {
        var h = a.Header;
        // 0100-INITIALISE
        h.Reset("PARMGET", "COM-025");
        a.ValueText = "";
        a.DataType = "";
        a.Unit = "";
        a.DecisionRef = "";
        a.OwnerRole = "";
        a.Description = "";
        a.Scope = "";
        a.ValueNum = 0;
        a.EffectiveFrom = 0;
        a.Found = "N";
        a.IsNumeric = "N";
        var code = CobolText.Upper(K.X(a.ParamCode, 40));
        a.ParamCode = code.TrimEnd(' ');
        if (a.AsOf == 0) a.AsOf = h.BusinessDate;
        if (a.AsOf == 0) a.AsOf = CobolDate.ToYyyymmdd(clock.GetLocalNow().DateTime);

        // 0200-VALIDATE
        if (K.Blank(code, 40))
        {
            h.Reject("PRM0001", "A parameter code is required");
            return h.Result;
        }
        if (CobolDate.TestDate(a.AsOf) != 0)
        {
            h.Reject("PRM0002", "The as of date is not a valid calendar date");
            return h.Result;
        }

        switch (h.FunctionCode)
        {
            case "GET":
                await ReadAsync(a, code, ct);
                break;
            case "GETNUM":
                await ReadAsync(a, code, ct);
                InsistNumeric(a, code);
                break;
            default:
                h.Reject("PRM0009", "PARMGET does not support function " + K.Trim(h.Function));
                break;
        }
        return h.Result;
    }

    /// <summary>1000-READ-PARAMETER: the company row wins over '*', then the latest EffectiveFrom on or before the as-of date.</summary>
    private async Task ReadAsync(ParmgetArea a, string code, CancellationToken ct)
    {
        var h = a.Header;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT TOP (1) p.ParamCode, p.CompanyCode, p.Value, p.DataType, p.UnitOfValue, p.EffectiveFrom, p.DecisionRef, " +
                "p.Description, p.OwnerRole FROM core.Parameter p WHERE p.ParamCode = ? AND p.CompanyCode IN (?, '*') " +
                "AND p.EffectiveFrom <= ? ORDER BY CASE WHEN p.CompanyCode = '*' THEN 1 ELSE 0 END, p.EffectiveFrom DESC",
                [SqlioParm.Text(code), SqlioParm.Text(K.Spaces(h.Company, 10) ? "*" : K.X(h.Company, 10)), SqlioParm.Digits(a.AsOf, 8)],
                [40, 10, 200, 12, 20, 10, 10, 100, 20], ct);
        }
        catch (SqlioException ex)
        {
            K.SqlFailed(h, ex, "PARMGET", "PRM0008");
            return;
        }
        if (!row.Found)
        {
            NotHeld(a, code);
            return;
        }
        a.Found = "Y";
        a.ValueText = row.Text(2);
        a.DataType = row.Text(3);
        a.Unit = row.Text(4);
        a.DecisionRef = row.Text(6);
        a.Description = row.Text(7);
        a.OwnerRole = row.Text(8);
        a.Scope = row.Raw(1) == K.X("*", 10) ? "ALL" : "COMPANY";
        a.EffectiveFrom = ConvertDate(row.Raw(5));
        ConvertNumber(a);
        h.Message = K.Msg(K.Trim(code), " = ", K.Trim(K.X(a.ValueText, 200)), " (", K.Trim(a.Scope), ")");
    }

    /// <summary>1200-NOT-HELD: a missing tuning parameter must not stop a business action when the caller offered a default.</summary>
    private static void NotHeld(ParmgetArea a, string code)
    {
        var h = a.Header;
        if (K.X(a.UseDefault, 1) == "Y")
        {
            a.ValueText = K.Store(a.DefaultText, 200);
            a.Scope = "DEFAULT";
            ConvertNumber(a);
            h.ReturnCode = 4;
            h.ReasonCode = "PRM0004";
            h.Message = K.Msg("Parameter ", K.Trim(code), " is not held - the supplied default was used");
            h.MessageCount++;
        }
        else
        {
            h.ReturnCode = 16;
            h.ReasonCode = "PRM0003";
            h.Message = K.Msg("Parameter ", K.Trim(code), " is not held for this company and date");
        }
    }

    /// <summary>1300-CONVERT-DATE: yyyy-mm-dd text to yyyymmdd (0 when not numeric).</summary>
    private static int ConvertDate(string slot)
    {
        if (CobolText.IsSpaces(slot)) return 0;
        var iso = K.X(slot, 10);
        var compact = iso[..4] + iso.Substring(5, 2) + iso.Substring(8, 2);
        return compact.All(char.IsAsciiDigit) ? int.Parse(compact, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>1400-CONVERT-NUMBER: numeric when at least one digit and only digits, '.', '-', '+', spaces or low-values (BRF-PARMGET-02).</summary>
    private static void ConvertNumber(ParmgetArea a)
    {
        a.ValueNum = 0;
        a.IsNumeric = "N";
        var text = K.X(a.ValueText, 200);
        var digits = 0;
        var bad = 0;
        foreach (var c in text)
        {
            if (c is >= '0' and <= '9') digits++;
            else if (c is '.' or '-' or '+' or ' ' or '\0') continue;
            else bad++;
        }
        if (digits > 0 && bad == 0)
        {
            a.IsNumeric = "Y";
            a.ValueNum = CobolNumeric.Store(CobolNumeric.NumVal(text), 13, 6);
        }
    }

    /// <summary>1500-INSIST-NUMERIC (skipped when RC is already above 04).</summary>
    private static void InsistNumeric(ParmgetArea a, string code)
    {
        var h = a.Header;
        if (h.ReturnCode > 4) return;
        if (a.IsNumeric == "N")
        {
            h.Reject("PRM0005", string.Concat("Parameter ", K.Trim(code), " is not numeric - value is [", K.Trim(K.X(a.ValueText, 200)), "]"));
        }
    }
}
