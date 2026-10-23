using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>EXCPRAISCA body (EXCPRAISCA.cpy:16-36).</summary>
public sealed class ExcpraisArea : Commarea
{
    /// <summary>EX-MODULE X(6) - upper-cased by the service.</summary>
    public string Module { get; set; } = "";

    /// <summary>EX-SEVERITY X(12): HOLD, WARNING or ERROR (upper-cased by the service).</summary>
    public string Severity { get; set; } = "";

    /// <summary>EX-CATEGORY X(40).</summary>
    public string Category { get; set; } = "";

    /// <summary>EX-MESSAGE-TEXT X(200).</summary>
    public string MessageText { get; set; } = "";

    /// <summary>EX-DOCUMENT-TYPE X(20).</summary>
    public string DocumentType { get; set; } = "";

    /// <summary>EX-DOCUMENT-REF X(40).</summary>
    public string DocumentRef { get; set; } = "";

    /// <summary>EX-OWNER-USER X(20).</summary>
    public string OwnerUser { get; set; } = "";

    /// <summary>EX-OWNER-ROLE X(20).</summary>
    public string OwnerRole { get; set; } = "";

    /// <summary>EX-PRIORITY S9(4): outside 1..9 replaced by 5.</summary>
    public int Priority { get; set; }

    /// <summary>EX-DUE-AT 9(8) yyyymmdd (0 = none).</summary>
    public int DueAt { get; set; }

    /// <summary>EX-AMOUNT-GBP S9(13)V9(6) (0 = NULL).</summary>
    public decimal AmountGbp { get; set; }

    /// <summary>EX-QUANTITY S9(13)V9(6) (0 = NULL).</summary>
    public decimal Quantity { get; set; }

    /// <summary>EX-EXCEPTION-ID S9(9) COMP-5 (carried as int64).</summary>
    public long ExceptionId { get; set; }

    /// <summary>EX-STATUS X(12).</summary>
    public string Status { get; set; } = "";

    /// <summary>EX-RAISED-AT X(23).</summary>
    public string RaisedAt { get; set; } = "";
}

/// <summary>EXCPRAIS - raise or refresh an owned worklist exception (FR-STERP-PLATFORM-017, COM-022).</summary>
public interface IExceptionRaiser
{
    /// <summary>CALL "EXCPRAIS" USING area: function RAISE (other EXC0009 after validation).</summary>
    Task<LegacyResult> ExecuteAsync(ExcpraisArea area, CancellationToken ct = default);

    /// <summary>
    /// RAISE through core.usp_RaiseException on the caller's unit of work. After a successful raise the return code mirrors
    /// the severity: HOLD 06, WARNING 04, ERROR 00 (SME-059, SME-1258).
    /// </summary>
    Task<ExcpraisArea> RaiseAsync(KernelCaller caller, ExcpraisArea body, CancellationToken ct = default);
}

/// <summary>EXCPRAIS.cob:59-280 on SQLIO slot 8.</summary>
public sealed class ExceptionRaiser(ISqlRunner sql) : IExceptionRaiser
{
    public async Task<ExcpraisArea> RaiseAsync(KernelCaller caller, ExcpraisArea body, CancellationToken ct = default)
    {
        body.Header.Prepare(caller, "RAISE");
        await ExecuteAsync(body, ct);
        return body;
    }

    public async Task<LegacyResult> ExecuteAsync(ExcpraisArea a, CancellationToken ct = default)
    {
        var h = a.Header;
        // 0100-INITIALISE
        h.Reset("EXCPRAIS", "COM-022");
        a.ExceptionId = 0;
        a.Status = "";
        a.RaisedAt = "";
        var severity = CobolText.Upper(K.X(a.Severity, 12));
        var module = CobolText.Upper(K.X(a.Module, 6));
        a.Severity = severity.TrimEnd(' ');
        a.Module = module.TrimEnd(' ');
        a.Priority = (int)CobolNumeric.Store(a.Priority, 4, 0);
        if (a.Priority is < 1 or > 9) a.Priority = 5;

        // 0200-VALIDATE (COM-017 - the three severities must stay distinguishable)
        if (K.Blank(h.Company, 10))
        {
            h.Reject("EXC0001", "An exception must belong to a company");
            return h.Result;
        }
        if (a.Severity is not ("HOLD" or "WARNING" or "ERROR"))
        {
            h.Reject("EXC0002", "Severity must be HOLD, WARNING or ERROR, not " + K.Trim(severity));
            return h.Result;
        }
        if (K.Blank(a.Category, 40))
        {
            h.Reject("EXC0003", "An exception must state its category");
            return h.Result;
        }
        if (K.Spaces(a.MessageText, 200))
        {
            h.Reject("EXC0004", "An exception must explain the affected action");
            return h.Result;
        }
        if (K.Spaces(a.OwnerUser, 20) && K.Spaces(a.OwnerRole, 20))
        {
            // COM-022 - an exception with no owner never gets worked
            h.Reject("EXC0005", "An exception needs an owning user or role");
            return h.Result;
        }
        if (a.DueAt != 0 && CobolDate.TestDate(a.DueAt) != 0)
        {
            h.Reject("EXC0006", "The due date is not a valid calendar date");
            return h.Result;
        }
        if (h.FunctionCode != "RAISE")
        {
            h.Reject("EXC0009", "EXCPRAIS does not support function " + K.Trim(h.Function));
            return h.Result;
        }

        // 1000-RAISE: parameters 6-10 and 15-16 NULL when blank; due date, amount and quantity NULL when 0
        var amount = CobolNumeric.Store(a.AmountGbp, 13, 6);
        var quantity = CobolNumeric.Store(a.Quantity, 13, 6);
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync("EXEC core.usp_RaiseException ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?",
                [
                    SqlioParm.Text(K.X(h.Company, 10)),
                    SqlioParm.Text(module),
                    SqlioParm.Text(severity),
                    SqlioParm.Text(K.X(a.Category, 40)),
                    SqlioParm.Text(K.X(a.MessageText, 200)),
                    SqlioParm.NullIfSpaces(K.X(a.DocumentType, 20)),
                    SqlioParm.NullIfSpaces(K.X(a.DocumentRef, 40)),
                    SqlioParm.NullIfSpaces(K.X(h.Site, 10)),
                    SqlioParm.NullIfSpaces(K.X(a.OwnerUser, 20)),
                    SqlioParm.NullIfSpaces(K.X(a.OwnerRole, 20)),
                    SqlioParm.Integer(a.Priority),
                    a.DueAt == 0 ? SqlioParm.Null : SqlioParm.Digits(a.DueAt, 8),
                    amount == 0 ? SqlioParm.Null : SqlioParm.Number(amount),
                    quantity == 0 ? SqlioParm.Null : SqlioParm.Number(quantity),
                    SqlioParm.NullIfSpaces(K.X(h.User, 20)),
                    SqlioParm.NullIfSpaces(K.X(h.CorrelationId, 36)),
                ],
                [20, 10, 6, 12, 40, 20, 40, 200, 20, 20, 8, 23, 23, 12], ct);
        }
        catch (SqlioException ex)
        {
            K.SqlFailed(h, ex, "EXCPRAIS", "EXC0008");
            return h.Result;
        }
        a.ExceptionId = (long)CobolNumeric.Store(row.NumVal(0), 18, 0);
        a.Status = row.Text(13);
        a.RaisedAt = row.Text(11);
        // the caller's own return code mirrors the severity so that a hold and a hard error cannot be confused
        h.ReturnCode = a.Severity switch
        {
            "HOLD" => 6,
            "WARNING" => 4,
            _ => 0,
        };
        h.Message = K.Msg(K.Trim(severity), " raised on worklist: ", K.Trim(K.X(a.MessageText, 200)));
        return h.Result;
    }
}
