using System.Text;

namespace Sterp.Platform.Hosting.PlatformContracts;

/// <summary>Row returned by core.usp_UiMaintSave (RowsAffected, EntityKey, Action).</summary>
public sealed record MaintenanceSaveResult(int RowsAffected, string? EntityKey, string Action);

/// <summary>
/// Public in-process contract IGenericMaintenance (STERP-PLATFORM 4.7, FR-STERP-PLATFORM-042): MaintService.Save - executes
/// core.usp_UiMaintSave(@MaintCode, @UserCode = session user, @CompanyCode = session company, @Mode upper-cased, @Data built by
/// <see cref="MaintenanceXml.ToXml"/>) once on its own SqlHelper connection (the procedure owns its transaction). Refusals
/// are thrown as BusinessRuleException (STERP-E-0090..0098); other database failures as SqlException. Registered by the
/// STERP-PLATFORM module; STERP-ITM uses it for ITM.ITEM and ITM.COUNTLINE (ItemService.vb:128, StockService.vb:147).
/// </summary>
public interface IGenericMaintenance
{
    /// <param name="values">column name -> value text, or null for a NULL value (null="1"); order is the XML element order</param>
    Task<MaintenanceSaveResult> SaveAsync(string maintCode, string mode, IReadOnlyList<KeyValuePair<string, string?>> values, CancellationToken ct = default);
}

public static class MaintenanceXml
{
    /// <summary>
    /// MaintService.ToXml (MaintService.vb:51-63): &lt;r&gt; then &lt;c n="Column" v="value" /&gt; per column, or
    /// &lt;c n="Column" null="1" /&gt; for a null value, then &lt;/r&gt;.
    /// </summary>
    public static string ToXml(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var sb = new StringBuilder("<r>");
        foreach (var (k, v) in values)
        {
            sb.Append("<c n=\"").Append(Esc(k)).Append('"');
            if (v is null) sb.Append(" null=\"1\" />");
            else sb.Append(" v=\"").Append(Esc(v)).Append("\" />");
        }
        sb.Append("</r>");
        return sb.ToString();
    }

    /// <summary>MaintService.Esc (MaintService.vb:65-68): &amp;, &lt;, &gt; and " escaped, nothing else.</summary>
    public static string Esc(string? s) =>
        s is null ? "" : s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}
