namespace Sterp.Platform.Kernel;

/// <summary>
/// Legacy STCAHDR result (cobol/copy/STCAHDR.cpy:12-32; dotnet/STERP.CobolInterop/CobolResult.cs:6-59).
/// Return codes: 00 OK, 04 WARNING, 06 HOLD, 08 ERROR, 12 SEVERE, 16 NOT FOUND. CA-FAILED = 08 THRU 16, so 06 is not a
/// failure (SME-038).
/// </summary>
public sealed record LegacyResult(int ReturnCode, string ReasonCode, string Message, int MessageCount = 0, string DocumentRef = "")
{
    public static LegacyResult Ok(string message = "", string reasonCode = "", int messageCount = 0, string documentRef = "") =>
        new(0, reasonCode, message, messageCount, documentRef);

    public static LegacyResult Warning(string reasonCode, string message, int messageCount = 1, string documentRef = "") =>
        new(4, reasonCode, message, messageCount, documentRef);

    public static LegacyResult Hold(string reasonCode, string message, int messageCount = 1, string documentRef = "") =>
        new(6, reasonCode, message, messageCount, documentRef);

    public static LegacyResult Error(string reasonCode, string message, int messageCount = 1, string documentRef = "") =>
        new(8, reasonCode, message, messageCount, documentRef);

    public static LegacyResult Severe(string reasonCode, string message, int messageCount = 1, string documentRef = "") =>
        new(12, reasonCode, message, messageCount, documentRef);

    public static LegacyResult NotFound(string reasonCode, string message, int messageCount = 1, string documentRef = "") =>
        new(16, reasonCode, message, messageCount, documentRef);

    public bool IsOk => ReturnCode == 0;
    public bool IsWarning => ReturnCode == 4;
    public bool IsHold => ReturnCode == 6;
    public bool IsNotFound => ReturnCode == 16;
    public bool IsSevere => ReturnCode == 12;

    /// <summary>CA-FAILED: 08 thru 16 (STCAHDR.cpy:27).</summary>
    public bool IsFailed => ReturnCode is >= 8 and <= 16;

    /// <summary>CobolResult.Succeeded: the engine accepted the request (00 or 04).</summary>
    public bool Succeeded => ReturnCode <= 4;

    public string Severity => LegacySeverity.Name(ReturnCode);

    /// <summary>CobolResult.Display: message (or severity name when blank) + two spaces + "(reason)" when a reason exists.</summary>
    public string Display
    {
        get
        {
            var m = (Message ?? "").Trim();
            if (m.Length == 0) m = Severity;
            var r = (ReasonCode ?? "").Trim();
            return r.Length > 0 ? $"{m}  ({r})" : m;
        }
    }

    /// <summary>Returns a copy with the legacy header field widths applied (reason X(8), message X(200), ref X(10)).</summary>
    public LegacyResult Normalized() => this with
    {
        ReasonCode = Cobol.CobolText.Fit(ReasonCode, 8).TrimEnd(),
        Message = Cobol.CobolText.Fit(Message, 200).TrimEnd(),
        DocumentRef = Cobol.CobolText.Fit(DocumentRef, 10).TrimEnd(),
    };
}

public static class LegacySeverity
{
    /// <summary>CobolResult.Severity (CobolResult.cs:27-43).</summary>
    public static string Name(int returnCode) => returnCode switch
    {
        0 => "OK",
        4 => "WARNING",
        6 => "HOLD",
        8 => "ERROR",
        12 => "SEVERE",
        16 => "NOT FOUND",
        _ => "RC" + returnCode.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>Kinds of RFC 9457 problems (contracts 4.7).</summary>
public static class ProblemKinds
{
    public const string BusinessRule = "business-rule";
    public const string Severe = "severe";
    public const string NotFound = "not-found";
    public const string Validation = "validation";
    public const string Forbidden = "forbidden";
    public const string Unauthenticated = "unauthenticated";
    public const string NotImplemented = "not-implemented";
}

/// <summary>Target-only reason codes (contracts 5.10).</summary>
public static class ApiReasons
{
    public const string MalformedHeader = "API0001";
    public const string CompanySiteNotAllowed = "API0002";
    public const string RunAsNotSysAdm = "API0003";
    public const string SchemaMismatch = "API0004";
    public const string NoUserMapped = "API0010";
    public const string NotAuthenticated = "API0401";
    public const string NotDelivered = "API0501";
    public const string ScreenGateRefused = "PRM0006";
}
