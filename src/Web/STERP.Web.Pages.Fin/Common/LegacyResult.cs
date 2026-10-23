using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Web.Framework.Api;

namespace Sterp.Web.Pages.Fin.Common;

/// <summary>
/// The CobolResult a legacy finance form inspected after a LINK (STERP.CobolInterop/CobolResult.cs): return code, reason
/// code, message and the COMMAREA the facade read whatever the return code was. A success carries it as the envelope
/// data, a business refusal (RC 8, 12, 16) as the problem's data. A failure without a return code is not a COBOL outcome
/// but an exception of the legacy facade: it is raised as <see cref="ApiFailure"/> for <c>GuardAsync</c> (SYS0001).
/// </summary>
public sealed class LegacyResult
{
    private LegacyResult(ApiResponse response, int returnCode, string reasonCode, string message, JsonNode? data)
    {
        Response = response;
        ReturnCode = returnCode;
        ReasonCode = reasonCode;
        Message = message;
        Data = data;
    }

    public ApiResponse Response { get; }

    public int ReturnCode { get; }

    public string ReasonCode { get; }

    public string Message { get; }

    /// <summary>The COMMAREA fields of the answer (empty object when the operation returned none).</summary>
    public JsonNode? Data { get; }

    public bool IsNotFound => ReturnCode == 16;

    public bool IsError => ReturnCode >= 8;

    /// <summary>True when the engine accepted the request (00 or 04).</summary>
    public bool Succeeded => ReturnCode <= 4;

    public string Severity => ReturnCode switch
    {
        0 => "OK",
        4 => "WARNING",
        6 => "HOLD",
        8 => "ERROR",
        12 => "SEVERE",
        16 => "NOT FOUND",
        _ => "RC" + ReturnCode.ToString("00", CultureInfo.InvariantCulture),
    };

    /// <summary>CobolResult.Display: the message (or the severity) with the reason code in brackets.</summary>
    public string Display
    {
        get
        {
            var m = Message.Trim();
            if (m.Length == 0) m = Severity;
            return ReasonCode.Trim().Length > 0 ? m + "  (" + ReasonCode.Trim() + ")" : m;
        }
    }

    public JsonNode? this[string field] => Data?[field];

    public static LegacyResult From(ApiResponse response)
    {
        if (response.IsSuccess)
        {
            var o = response.Outcome;
            return new LegacyResult(response, o?.ReturnCode ?? 0, o?.ReasonCode ?? "", o?.Message ?? "", response.Data);
        }
        var p = response.Problem!;
        if (p.ReturnCode is not { } rc) throw new ApiFailure(response);
        return new LegacyResult(response, rc, p.ReasonCode, p.Detail, p.Data);
    }
}
