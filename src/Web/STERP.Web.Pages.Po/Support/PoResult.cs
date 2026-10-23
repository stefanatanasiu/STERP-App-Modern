using System.Text.Json.Nodes;
using Sterp.Web.Framework.Api;

namespace Sterp.Web.Pages.Po.Support;

/// <summary>
/// The legacy <c>CobolResult</c> (dotnet/STERP.CobolInterop/CobolResult.cs) of one facade call, rebuilt from the API answer:
/// the outcome envelope of RC 0/4/6, or the problem of RC 8/12/16 (with the COMMAREA in <c>data</c> where the operation
/// carries it, spec rule R4). A failure without a legacy return code is not a COBOL outcome (the facade threw) and never
/// becomes a <see cref="PoResult"/>.
/// </summary>
public sealed record PoResult(int ReturnCode, string ReasonCode, string Message, string Severity, JsonNode? Data)
{
    public bool IsOk => ReturnCode == 0;

    public bool IsWarning => ReturnCode == 4;

    public bool IsHold => ReturnCode == 6;

    public bool IsNotFound => ReturnCode == 16;

    public bool IsError => ReturnCode >= 8;

    public bool IsSevere => ReturnCode == 12;

    /// <summary>True when the engine accepted the request (00 or 04).</summary>
    public bool Succeeded => ReturnCode <= 4;

    /// <summary>CobolResult.Display: the message (or the severity) with the reason code in brackets (COM-017).</summary>
    public string Display
    {
        get
        {
            var m = Message.Trim();
            if (m.Length == 0) m = SeverityText;
            return ReasonCode.Trim().Length > 0 ? m + "  (" + ReasonCode.Trim() + ")" : m;
        }
    }

    /// <summary>CobolResult.Severity.</summary>
    public string SeverityText => ReturnCode switch
    {
        0 => "OK",
        4 => "WARNING",
        6 => "HOLD",
        8 => "ERROR",
        12 => "SEVERE",
        16 => "NOT FOUND",
        _ => "RC" + ReturnCode.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>True when the answer is a facade exception rather than a COBOL outcome (no legacy return code).</summary>
    public static bool IsException(ApiResponse r) => !r.IsSuccess && r.Problem!.ReturnCode is null;

    /// <summary>The COBOL outcome of a call that is not a facade exception (see <see cref="IsException"/>).</summary>
    public static PoResult From(ApiResponse r)
    {
        if (r.IsSuccess)
        {
            var o = r.Outcome;
            return o is null
                ? new PoResult(0, "", "", "OK", r.Data)
                : new PoResult(o.ReturnCode, o.ReasonCode, o.Message, o.Severity, r.Data);
        }
        var p = r.Problem!;
        return new PoResult(p.ReturnCode ?? 12, p.ReasonCode, Sterp.Web.Framework.Session.Json.Str(r.Json?["detail"]), p.Severity, p.Data);
    }
}
