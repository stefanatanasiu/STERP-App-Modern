using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>AUDITLOGCA body (AUDITLOGCA.cpy:16-28).</summary>
public sealed class AuditlogArea : Commarea
{
    /// <summary>AU-ENTITY-TYPE X(60).</summary>
    public string EntityType { get; set; } = "";

    /// <summary>AU-ENTITY-KEY X(120).</summary>
    public string EntityKey { get; set; } = "";

    /// <summary>AU-ACTION X(20) - upper-cased by the service.</summary>
    public string Action { get; set; } = "";

    /// <summary>AU-FIELD-NAME X(60) (NULL when blank).</summary>
    public string FieldName { get; set; } = "";

    /// <summary>AU-OLD-VALUE X(200) (NULL when blank).</summary>
    public string OldValue { get; set; } = "";

    /// <summary>AU-NEW-VALUE X(200) (NULL when blank).</summary>
    public string NewValue { get; set; } = "";

    /// <summary>AU-REASON X(200) (NULL when blank).</summary>
    public string Reason { get; set; } = "";

    /// <summary>AU-AUTHORITY-REF X(60) (NULL when blank).</summary>
    public string AuthorityRef { get; set; } = "";

    /// <summary>AU-SOURCE X(20): blank defaults to COBOL.</summary>
    public string Source { get; set; } = "";

    /// <summary>AU-SENSITIVE X: anything but "Y" becomes "N".</summary>
    public string Sensitive { get; set; } = "";

    /// <summary>AU-AUDIT-ID S9(9) COMP-5 (carried as int64): the new core.AuditLog row.</summary>
    public long AuditId { get; set; }
}

/// <summary>AUDITLOG - attributable history of a material change (FR-STERP-PLATFORM-016, COM-013).</summary>
public interface IAuditWriter
{
    /// <summary>CALL "AUDITLOG" USING area: function WRITE (other AUD0009 after validation).</summary>
    Task<LegacyResult> ExecuteAsync(AuditlogArea area, CancellationToken ct = default);

    /// <summary>WRITE one core.AuditLog row through core.usp_WriteAudit on the caller's unit of work.</summary>
    Task<AuditlogArea> WriteAsync(KernelCaller caller, AuditlogArea body, CancellationToken ct = default);
}

/// <summary>AUDITLOG.cob:46-201 on SQLIO slot 8.</summary>
public sealed class AuditWriter(ISqlRunner sql) : IAuditWriter
{
    public async Task<AuditlogArea> WriteAsync(KernelCaller caller, AuditlogArea body, CancellationToken ct = default)
    {
        body.Header.Prepare(caller, "WRITE");
        await ExecuteAsync(body, ct);
        return body;
    }

    public async Task<LegacyResult> ExecuteAsync(AuditlogArea a, CancellationToken ct = default)
    {
        var h = a.Header;
        // 0100-INITIALISE
        h.Reset("AUDITLOG", "COM-013");
        a.AuditId = 0;
        if (K.Blank(a.Source, 20)) a.Source = "COBOL";
        a.Sensitive = K.X(a.Sensitive, 1) == "Y" ? "Y" : "N";
        var action = CobolText.Upper(K.X(a.Action, 20));
        a.Action = action.TrimEnd(' ');

        // 0200-VALIDATE (entity type and key are tested against SPACES only, BRF-AUDITLOG-02)
        if (K.Blank(h.User, 20))
        {
            h.Reject("AUD0001", "An audit row must name the user responsible");
            return h.Result;
        }
        if (K.Spaces(a.EntityType, 60) || K.Spaces(a.EntityKey, 120))
        {
            h.Reject("AUD0002", "An audit row must identify the record changed");
            return h.Result;
        }
        if (K.Blank(action, 20))
        {
            h.Reject("AUD0003", "An audit row must state the action taken");
            return h.Result;
        }
        if (h.FunctionCode != "WRITE")
        {
            h.Reject("AUD0009", "AUDITLOG does not support function " + K.Trim(h.Function));
            return h.Result;
        }

        // 1000-WRITE-AUDIT: parameters 6-12 are NULL when blank (1100-NULL-OPTIONALS)
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync("EXEC core.usp_WriteAudit ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?",
                [
                    SqlioParm.Text(K.X(h.User, 20)),
                    SqlioParm.Text(K.X(h.Company, 10)),
                    SqlioParm.Text(K.X(a.EntityType, 60)),
                    SqlioParm.Text(K.X(a.EntityKey, 120)),
                    SqlioParm.Text(action),
                    SqlioParm.NullIfSpaces(K.X(a.FieldName, 60)),
                    SqlioParm.NullIfSpaces(K.X(a.OldValue, 200)),
                    SqlioParm.NullIfSpaces(K.X(a.NewValue, 200)),
                    SqlioParm.NullIfSpaces(K.X(a.Reason, 200)),
                    SqlioParm.NullIfSpaces(K.X(a.AuthorityRef, 60)),
                    SqlioParm.NullIfSpaces(K.X(a.Source, 20)),
                    SqlioParm.NullIfSpaces(K.X(h.CorrelationId, 36)),
                    SqlioParm.Text(a.Sensitive == "Y" ? "1" : "0"),
                ],
                [20], ct);
        }
        catch (SqlioException ex)
        {
            K.SqlFailed(h, ex, "AUDITLOG", "AUD0008");
            return h.Result;
        }
        // a no-row answer reads a fresh row area (the previous call's row is not carried over, BC-PLATFORM-07)
        a.AuditId = (long)CobolNumeric.Store(row.NumVal(0), 18, 0);
        h.Message = K.Msg("Audited ", K.Trim(action), " of ", K.Trim(K.X(a.EntityType, 60)), " ", K.Trim(K.X(a.EntityKey, 120)));
        return h.Result;
    }
}
