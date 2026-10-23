using System.Globalization;
using System.Reflection;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Engine;

/// <summary>
/// The STCAHDR header of an AP COMMAREA (cobol/copy/STCAHDR.cpy) as CobolGateway.PrepareHeader fills it
/// (CobolGateway.cs:69-83): program and function upper-cased, company/site/user from the session, business date,
/// correlation id, return fields cleared. The program updates the return fields; <see cref="Result"/> is what the facade
/// reads back (CA-REQ-REF is published as documentRef).
/// </summary>
public sealed class CaHeader
{
    public string Program { get; set; } = "";
    public string Function { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public int BusinessDate { get; set; }
    public string CorrelationId { get; set; } = "";
    public int ReturnCode { get; set; }
    public string ReasonCode { get; set; } = "";
    public string Message { get; set; } = "";
    public string ReqRef { get; set; } = "";
    public int MsgCount { get; set; }

    public static CaHeader For(SterpContext session, string program, string function, string? correlationId = null) => new()
    {
        Program = CobolText.Trimmed(program.ToUpperInvariant(), 8),
        Function = CobolText.Trimmed(function.ToUpperInvariant(), 8),
        Company = CobolText.Trimmed(session.CompanyCode, 10),
        Site = CobolText.Trimmed(session.SiteCode, 10),
        User = CobolText.Trimmed(session.UserCode, 20),
        BusinessDate = CobolDate.ToYyyymmdd(session.BusinessDate),
        CorrelationId = CobolText.Trimmed(correlationId ?? session.CorrelationId, 36),
    };

    /// <summary>CA-FAILED: 08 thru 16.</summary>
    public bool Failed => ReturnCode is >= 8 and <= 16;

    /// <summary>MOVE text TO CA-MESSAGE (X(200)).</summary>
    public void SetMessage(string text) => Message = CobolText.Trimmed(text, 200);

    public LegacyResult Result => new(ReturnCode, CobolText.Trimmed(ReasonCode, 8), CobolText.Trimmed(Message, 200), MsgCount,
        CobolText.Trimmed(ReqRef, 10));
}

/// <summary>
/// SQLIO calls of the AP programs on the request's engine unit of work through the kernel SQLIO helper
/// (Sterp.Platform.Kernel.Data.Sqlio): statement parameters @p1..@pn bound as varchar text in declaration order (string ->
/// trimmed text, null -> NULL, integer -> SQLIO-INT-EDIT, decimal -> SQLIO-NUM-EDIT), columns fetched as ODBC text into
/// fixed-width slots (NULL -> spaces).
/// </summary>
public static class ApSqlio
{
    /// <summary>SNGL: the first row as text slots, or null when no row (SQLIO-NOTFOUND).</summary>
    public static async Task<string[]?> SingleAsync(ISqlRunner sql, string statement, object? param, int[] widths, CancellationToken ct = default)
    {
        var row = await sql.SingleAsync(statement, Parms(param), widths, ct);
        return row.Found ? row.Slots.ToArray() : null;
    }

    /// <summary>EXEC + FTCH loop: every row as text slots.</summary>
    public static async Task<List<string[]>> RowsAsync(ISqlRunner sql, string statement, object? param, int[] widths, CancellationToken ct = default) =>
        (await sql.FetchAllAsync(statement, Parms(param), widths, ct)).Select(r => r.Slots.ToArray()).ToList();

    /// <summary>EXEC without fetch: SQLIO-ROWCOUNT.</summary>
    public static async Task<int> ExecAsync(ISqlRunner sql, string statement, object? param, CancellationToken ct = default) =>
        (await sql.ExecAsync(statement, Parms(param), ct)).RowCount;

    private static List<SqlioParm> Parms(object? param)
    {
        var list = new List<SqlioParm>();
        if (param is null) return list;
        foreach (var p in param.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            list.Add(p.GetValue(param) switch
            {
                null => SqlioParm.Null,
                string s => SqlioParm.Text(s),
                int i => SqlioParm.Integer(i),
                long l => SqlioParm.Integer(l),
                decimal d => SqlioParm.Number(d),
                var other => SqlioParm.Text(Convert.ToString(other, CultureInfo.InvariantCulture)),
            });
        }
        return list;
    }

    /// <summary>FUNCTION NUMVAL of a text slot (spaces -> 0).</summary>
    public static decimal NumVal(string? text) => CobolNumeric.NumVal(text ?? "");

    /// <summary>A text slot right-trimmed (the API representation of a PIC X field).</summary>
    public static string T(string? slot) => (slot ?? "").TrimEnd(' ');
}
