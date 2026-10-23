using System.Text.Json.Nodes;
using Sterp.Web.Framework.Api;
using Sterp.Web.Framework.Session;

// Declared in the parent namespace of the slice page libraries (Sterp.Web.Pages.<Slice>.*), not in Sterp.Web.Framework.Api:
// every page sees it without a using, and a library that brings its own LegacyResult through its _Imports.razor (FIN:
// Sterp.Web.Pages.Fin.Common.LegacyResult) keeps that type without an ambiguity, because types imported by the using
// directives of the page's own namespace are found before the members of an enclosing namespace.
namespace Sterp.Web.Pages;

/// <summary>
/// The legacy <c>CobolResult</c> view of an API answer (CobolResult.cs): return code, reason code, message and the COMMAREA
/// data, whether the call succeeded (200/202 envelope) or failed (problem carrying the same <c>data</c>, contracts 4.7).
/// Pages use it where a legacy handler read the COMMAREA or <c>r.Display</c> itself instead of <c>ShowResult</c>.
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

    /// <summary>The COMMAREA object (envelope <c>data</c>, or the problem's <c>data</c>); null when the answer carries none.</summary>
    public JsonNode? Data { get; }

    /// <summary>CobolResult.Succeeded: the engine accepted the request (RC 00 or 04).</summary>
    public bool Succeeded => ReturnCode <= 4;

    public string Severity => ReturnCode switch
    {
        0 => "OK",
        4 => "WARNING",
        6 => "HOLD",
        8 => "ERROR",
        12 => "SEVERE",
        16 => "NOT FOUND",
        _ => "RC" + ReturnCode.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>CobolResult.Display: the message (or the severity) with the reason code in brackets (COM-017).</summary>
    public string Display
    {
        get
        {
            var m = Message.Trim();
            if (m.Length == 0) m = Severity;
            return ReasonCode.Trim().Length > 0 ? m + "  (" + ReasonCode.Trim() + ")" : m;
        }
    }

    public string Str(string name) => Json.Str(Data?[name]);

    public int Int(string name) => Json.Int(Data?[name]);

    public decimal Dec(string name) => Json.Dec(Data?[name]);

    public static LegacyResult From(ApiResponse r)
    {
        if (r.IsSuccess)
        {
            var o = r.Outcome;
            return new LegacyResult(r, o?.ReturnCode ?? 0, o?.ReasonCode ?? "", o?.Message ?? "", r.Data);
        }
        var p = r.Problem!;
        var rc = p.ReturnCode ?? (r.Status == 404 ? 16 : r.Status >= 500 || r.NetworkError is not null ? 12 : 8);
        return new LegacyResult(r, rc, p.ReasonCode, p.Detail, p.Data);
    }
}
