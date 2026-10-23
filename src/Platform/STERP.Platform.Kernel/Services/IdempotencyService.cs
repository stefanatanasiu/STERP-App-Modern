using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>IDEMPOTCA body (IDEMPOTCA.cpy:23-36).</summary>
public sealed class IdempotArea : Commarea
{
    /// <summary>ID-OPERATION X(60) - required for BEGIN.</summary>
    public string Operation { get; set; } = "";

    /// <summary>ID-STATUS X(12) - COMPLETED or FAILED for END (upper-cased by the service).</summary>
    public string Status { get; set; } = "";

    /// <summary>ID-OUTCOME X(12): STARTED, COMPLETED, INPROGRESS (BEGIN) or the stored status (END).</summary>
    public string Outcome { get; set; } = "";

    /// <summary>ID-RESULT-REF X(60).</summary>
    public string ResultRef { get; set; } = "";

    /// <summary>ID-RESULT-MESSAGE X(200).</summary>
    public string ResultMessage { get; set; } = "";

    /// <summary>ID-STARTED-AT X(23) (yyyy-mm-dd hh:mm:ss.fff as the driver rendered it).</summary>
    public string StartedAt { get; set; } = "";

    /// <summary>ID-ENDED-AT X(23).</summary>
    public string EndedAt { get; set; } = "";
}

/// <summary>IDEMPOT - instruction idempotency (FR-STERP-PLATFORM-015, COM-015).</summary>
public interface IIdempotencyService
{
    /// <summary>CALL "IDEMPOT" USING area: functions BEGIN and END (other IDM0004 after validation).</summary>
    Task<LegacyResult> ExecuteAsync(IdempotArea area, CancellationToken ct = default);

    /// <summary>
    /// BEGIN: claims the caller's correlation id through core.usp_IdempotentBegin. STARTED RC 00, COMPLETED RC 04 IDM0006
    /// (original result reference), INPROGRESS RC 06 IDM0007. The claim commits at once under autocommit when the caller
    /// has not begun a unit (an abandoned claim keeps answering INPROGRESS, SME-061).
    /// </summary>
    Task<IdempotArea> BeginAsync(KernelCaller caller, string operation, CancellationToken ct = default);

    /// <summary>END: records COMPLETED or FAILED with the result reference and message (core.usp_IdempotentEnd).</summary>
    Task<IdempotArea> EndAsync(KernelCaller caller, string status, string? resultRef = null, string? resultMessage = null, CancellationToken ct = default);
}

/// <summary>IDEMPOT.cob:61-312 on SQLIO slot 7 (BRF-IDEMPOT-01: the FAILED and unknown-outcome branches stay unreachable).</summary>
public sealed class IdempotencyService(ISqlRunner sql) : IIdempotencyService
{
    public async Task<IdempotArea> BeginAsync(KernelCaller caller, string operation, CancellationToken ct = default)
    {
        var area = new IdempotArea { Operation = operation };
        area.Header.Prepare(caller, "BEGIN");
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<IdempotArea> EndAsync(KernelCaller caller, string status, string? resultRef = null, string? resultMessage = null, CancellationToken ct = default)
    {
        var area = new IdempotArea { Status = status, ResultRef = resultRef ?? "", ResultMessage = resultMessage ?? "" };
        area.Header.Prepare(caller, "END");
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<LegacyResult> ExecuteAsync(IdempotArea a, CancellationToken ct = default)
    {
        var h = a.Header;
        var fn = h.FunctionCode;
        // 0100-INITIALISE
        h.Reset("IDEMPOT", "COM-015");
        a.Outcome = "";
        if (fn == "BEGIN")
        {
            a.ResultRef = "";
            a.ResultMessage = "";
            a.StartedAt = "";
            a.EndedAt = "";
        }

        // 0200-VALIDATE
        var correlation = K.X(h.CorrelationId, 36);
        if (K.Blank(correlation, 36))
        {
            h.Reject("IDM0001", "A correlation id is required");
            return h.Result;
        }
        if (fn == "BEGIN" && K.Blank(a.Operation, 60))
        {
            h.Reject("IDM0002", "The instruction must name its operation");
            return h.Result;
        }
        if (fn == "END")
        {
            var status = CobolText.Upper(K.X(a.Status, 12));
            a.Status = status.TrimEnd(' ');
            if (a.Status is not ("COMPLETED" or "FAILED"))
            {
                h.Reject("IDM0003", "An instruction ends COMPLETED or FAILED");
                return h.Result;
            }
        }

        switch (fn)
        {
            case "BEGIN":
                await BeginInternalAsync(a, correlation, ct);
                break;
            case "END":
                await EndInternalAsync(a, correlation, ct);
                break;
            default:
                h.Reject("IDM0004", "IDEMPOT does not support function " + K.Trim(h.Function));
                break;
        }
        return h.Result;
    }

    /// <summary>1000-BEGIN and 1100-REPORT-OUTCOME.</summary>
    private async Task BeginInternalAsync(IdempotArea a, string correlation, CancellationToken ct)
    {
        var h = a.Header;
        var operation = K.X(a.Operation, 60);
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync("EXEC core.usp_IdempotentBegin ?, ?, ?, ?",
                [SqlioParm.Text(correlation), SqlioParm.Text(operation),
                 SqlioParm.NullIfSpaces(K.X(h.Company, 10)), SqlioParm.NullIfSpaces(K.X(h.User, 20))],
                [64, 60, 12, 60, 200], ct);
        }
        catch (SqlioException ex)
        {
            K.SqlFailed(h, ex, "IDEMPOT", "IDM0010");
            return;
        }
        if (!row.Found)
        {
            h.ReasonCode = "IDM0005";
            h.Message = "The idempotency register gave no answer";
            h.ReturnCode = 12;
            return;
        }
        var outcome = CobolText.Upper(row.Raw(2)).TrimEnd(' ');
        // core.usp_IdempotentBegin speaks NEW / DUPLICATE; the COMMAREA vocabulary is STARTED / INPROGRESS / COMPLETED / FAILED
        outcome = outcome switch
        {
            "NEW" => "STARTED",
            "DUPLICATE" => "COMPLETED",
            _ => outcome,
        };
        a.Outcome = outcome;
        a.ResultRef = row.Text(3);
        a.ResultMessage = row.Text(4);

        h.Message = "";
        switch (outcome)
        {
            case "STARTED":
                h.ReturnCode = 0;
                h.Message = K.Msg("Instruction claimed - carry on with ", K.Trim(operation));
                break;
            case "COMPLETED":
                h.ReturnCode = 4;
                h.ReasonCode = "IDM0006";
                h.Message = K.Msg("This instruction was already completed - the original result is ", K.Trim(a.ResultRef));
                h.MessageCount++;
                break;
            case "INPROGRESS":
                // an uncertain outcome is neither success nor a safe to repeat failure - the user must establish it
                h.ReturnCode = 6;
                h.ReasonCode = "IDM0007";
                h.Message = K.Msg("Another attempt at this instruction is still running - establish its outcome before starting a replacement");
                h.MessageCount++;
                break;
            case "FAILED":
                // unreachable: usp_IdempotentBegin resets a FAILED key to NEW (BRF-IDEMPOT-01)
                h.ReturnCode = 8;
                h.ReasonCode = "IDM0008";
                h.Message = K.Msg("The earlier attempt failed: ", K.Trim(a.ResultMessage));
                h.MessageCount++;
                break;
            default:
                h.ReturnCode = 12;
                h.ReasonCode = "IDM0009";
                h.Message = K.Msg("Unknown idempotency outcome ", K.Trim(outcome));
                h.MessageCount++;
                break;
        }
    }

    /// <summary>2000-END.</summary>
    private async Task EndInternalAsync(IdempotArea a, string correlation, CancellationToken ct)
    {
        var h = a.Header;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync("EXEC core.usp_IdempotentEnd ?, ?, ?, ?",
                [SqlioParm.Text(correlation), SqlioParm.Text(K.X(a.Status, 12)),
                 SqlioParm.NullIfSpaces(K.X(a.ResultRef, 60)), SqlioParm.NullIfSpaces(K.X(a.ResultMessage, 200))],
                [64, 60, 12, 60, 200, 23, 23], ct);
        }
        catch (SqlioException ex)
        {
            K.SqlFailed(h, ex, "IDEMPOT", "IDM0010");
            return;
        }
        // no row is unreachable (the procedure THROWs); a fresh row area is used rather than the previous call's
        a.Outcome = row.Text(2);
        a.ResultRef = row.Text(3);
        a.StartedAt = row.Text(5);
        a.EndedAt = row.Text(6);
        h.Message = K.Msg("Instruction recorded as ", K.Trim(a.Status));
    }
}
