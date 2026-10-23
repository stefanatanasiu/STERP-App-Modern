using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Sterp.Platform.Kernel;

/// <summary>
/// A stored procedure refused the request with THROW 'STERP-&lt;s&gt;-&lt;code&gt;: text'
/// (dotnet/STERP.Data/BusinessRuleException.cs:7-44). Maps to 422 business-rule (contracts 4.6).
/// </summary>
public sealed partial class BusinessRuleException(string code, char severity, string text, string? procedure, Exception? inner)
    : Exception(text, inner)
{
    /// <summary>Stable message id, e.g. "E-0042".</summary>
    public string Code { get; } = code;

    /// <summary>'E' error, 'W' warning, 'I' information.</summary>
    public char Severity { get; } = severity;

    public string? ProcedureName { get; } = procedure;

    /// <summary>
    /// Returns a BusinessRuleException when any server error carries a STERP message id (first match), otherwise null
    /// (BusinessRuleException.TryTranslate, BusinessRuleException.cs:30-44).
    /// </summary>
    public static BusinessRuleException? TryTranslate(SqlException? ex)
    {
        if (ex is null) return null;
        foreach (SqlError e in ex.Errors)
        {
            var m = Tag().Match(e.Message ?? "");
            if (!m.Success) continue;
            return new BusinessRuleException(m.Groups["s"].Value + "-" + m.Groups["c"].Value,
                m.Groups["s"].Value[0], m.Groups["t"].Value.Trim(), e.Procedure, ex);
        }
        return null;
    }

    /// <summary>Same translation applied to message text (used where the legacy COBOL program inspected SQLIO-ERRMSG).</summary>
    public static Match MatchText(string? text) => Tag().Match(text ?? "");

    [GeneratedRegex(@"STERP-(?<s>[EWI])-(?<c>[A-Za-z0-9]{1,8})\s*:\s*(?<t>.*)")]
    private static partial Regex Tag();
}

/// <summary>
/// A COBOL-derived path ended with a legacy return code (any code). The host maps it to the envelope or problem of
/// contracts 4.6 and carries <see cref="Data"/> for refusals whose legacy screen read the COMMAREA (contracts 4.7).
/// </summary>
public sealed class LegacyOutcomeException(LegacyResult result, object? data = null)
    : Exception(result.Display)
{
    public LegacyResult Result { get; } = result;
    public object? Payload { get; } = data;
}

/// <summary>
/// SQLIO failure on a COBOL-derived path: SQLCODE -1 with the ODBC diagnostic text (sterpsql.c diag(), SQLIO.cob:77-85).
/// Uncaught, it becomes RC 12 SQL0001 with that text (contracts 6.6).
/// </summary>
public sealed class SqlioException(string driverText, string sqlState, SqlException inner)
    : Exception(driverText, inner)
{
    /// <summary>The legacy SQLIO-ERRMSG text (prefix + server message, at most 512 bytes).</summary>
    public string DriverText { get; } = driverText;

    public string SqlState { get; } = sqlState;

    public SqlException SqlError => (SqlException)InnerException!;

    /// <summary>The server message of the first error (no driver prefix).</summary>
    public string ServerMessage => SqlError.Errors.Count > 0 ? SqlError.Errors[0].Message : SqlError.Message;

    public int Number => SqlError.Number;
}

/// <summary>Target-only problem (contracts 4.7, 5.10): status, kind and reserved reason code.</summary>
public sealed class ApiProblemException(int status, string kind, string reasonCode, string detail, string? title = null)
    : Exception(detail)
{
    public int Status { get; } = status;
    public string Kind { get; } = kind;
    public string ReasonCode { get; } = reasonCode;
    public string Detail { get; } = detail;
    public string? Title { get; } = title;
    public IDictionary<string, string[]>? Errors { get; init; }

    public static ApiProblemException BadHeader(string detail) => new(400, ProblemKinds.Validation, ApiReasons.MalformedHeader, detail, "Malformed or missing context header");
    public static ApiProblemException BadBody(string detail, IDictionary<string, string[]>? errors = null) =>
        new(400, ProblemKinds.Validation, ApiReasons.SchemaMismatch, detail, "Request body does not match the schema") { Errors = errors };
    public static ApiProblemException Forbidden(string reasonCode, string detail) => new(403, ProblemKinds.Forbidden, reasonCode, detail, "Forbidden");
    public static ApiProblemException NotDelivered(string detail) => new(501, ProblemKinds.NotImplemented, ApiReasons.NotDelivered, detail, "Not yet delivered in the modernized application");
}
