using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>NUMGENCA body (NUMGENCA.cpy:18-25).</summary>
public sealed class NumgenArea : Commarea
{
    /// <summary>NG-SERIES-CODE X(20) - upper-cased by the service.</summary>
    public string SeriesCode { get; set; } = "";

    /// <summary>NG-DOCUMENT-NUMBER X(30): the drawn/replayed number, or the number to void.</summary>
    public string DocumentNumber { get; set; } = "";

    /// <summary>NG-SEQUENCE S9(9) COMP-5 (carried as int64).</summary>
    public long Sequence { get; set; }

    /// <summary>NG-REASON-TEXT X(200): the void reason.</summary>
    public string ReasonText { get; set; } = "";

    /// <summary>NG-STATUS X(12): ISSUED, VOID, or the stored status of a replayed number.</summary>
    public string Status { get; set; } = "";
}

/// <summary>NUMGEN - document numbering (FR-STERP-PLATFORM-014, COM-005, COM-015).</summary>
public interface IDocumentNumberService
{
    /// <summary>CALL "NUMGEN" USING area: functions NEXT and VOID (other NUM0009 after validation).</summary>
    Task<LegacyResult> ExecuteAsync(NumgenArea area, CancellationToken ct = default);

    /// <summary>
    /// NEXT: replays the number already issued for the caller's correlation id (RC 04 NUM0007) or draws one through
    /// core.usp_NextDocumentNumber on the caller's unit of work - outside the caller's unit exactly when the caller has
    /// not begun one (the procedure owns a transaction only when none is open).
    /// </summary>
    Task<NumgenArea> NextAsync(KernelCaller caller, string seriesCode, CancellationToken ct = default);

    /// <summary>VOID: core.usp_VoidDocumentNumber (reason required).</summary>
    Task<NumgenArea> VoidAsync(KernelCaller caller, string seriesCode, string documentNumber, string reason, CancellationToken ct = default);
}

/// <summary>NUMGEN.cob:61-328 on SQLIO slot 7.</summary>
public sealed class DocumentNumberService(ISqlRunner sql) : IDocumentNumberService
{
    public async Task<NumgenArea> NextAsync(KernelCaller caller, string seriesCode, CancellationToken ct = default)
    {
        var area = new NumgenArea { SeriesCode = seriesCode };
        area.Header.Prepare(caller, "NEXT");
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<NumgenArea> VoidAsync(KernelCaller caller, string seriesCode, string documentNumber, string reason, CancellationToken ct = default)
    {
        var area = new NumgenArea { SeriesCode = seriesCode, DocumentNumber = documentNumber, ReasonText = reason };
        area.Header.Prepare(caller, "VOID");
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<LegacyResult> ExecuteAsync(NumgenArea a, CancellationToken ct = default)
    {
        var h = a.Header;
        // 0100-INITIALISE
        h.Reset("NUMGEN", "COM-005");
        a.Status = "";
        var series = CobolText.Upper(K.X(a.SeriesCode, 20));
        a.SeriesCode = series.TrimEnd(' ');
        var fn = h.FunctionCode;

        // 0200-VALIDATE
        if (K.Blank(h.Company, 10))
        {
            h.Reject("NUM0001", "A company code is required for numbering");
            return h.Result;
        }
        if (K.Blank(series, 20))
        {
            h.Reject("NUM0002", "A document series code is required");
            return h.Result;
        }
        if (fn == "VOID")
        {
            if (K.Spaces(a.DocumentNumber, 30))
            {
                h.Reject("NUM0003", "The document number to void is required");
                return h.Result;
            }
            if (K.Spaces(a.ReasonText, 200))
            {
                // COM-013 - a void without a reason leaves no evidence
                h.Reject("NUM0004", "A reason is required to void a reference");
                return h.Result;
            }
        }

        switch (fn)
        {
            case "NEXT":
                await NextNumberAsync(a, series, ct);
                break;
            case "VOID":
                await VoidNumberAsync(a, series, ct);
                break;
            default:
                h.Reject("NUM0009", "NUMGEN does not support function " + K.Trim(h.Function));
                break;
        }
        return h.Result;
    }

    /// <summary>1000-NEXT-NUMBER.</summary>
    private async Task NextNumberAsync(NumgenArea a, string series, CancellationToken ct)
    {
        var h = a.Header;
        a.DocumentNumber = "";
        a.Sequence = 0;
        var company = K.X(h.Company, 10);
        var correlation = K.X(h.CorrelationId, 36);
        // COM-015 - the replay is decided here before any number is drawn; replay and draw are separate statements without
        // a shared lock (SME-060, SME-1259)
        if (!CobolText.IsSpaces(correlation))
        {
            SqlioRow replay;
            try
            {
                replay = await sql.SingleAsync(
                    "SELECT TOP (1) DocumentNumber, CAST(SequenceNo AS varchar(12)), Status FROM core.DocumentNumber " +
                    "WHERE CompanyCode = ? AND SeriesCode = ? AND CorrelationId = ? ORDER BY SequenceNo",
                    [SqlioParm.Text(company), SqlioParm.Text(series), SqlioParm.Text(correlation)],
                    [30, 12, 12], ct);
            }
            catch (SqlioException ex)
            {
                // a failed replay lookup prevents any draw
                K.SqlFailed(h, ex, "NUMGEN", "NUM0008");
                return;
            }
            if (replay.Found)
            {
                a.DocumentNumber = replay.Text(0);
                a.Sequence = (long)CobolNumeric.Store(replay.NumVal(1), 18, 0);
                a.Status = replay.Text(2);
                h.ReturnCode = 4;
                h.ReasonCode = "NUM0007";
                h.MessageCount++;
                h.Message = K.Msg("Reference ", K.Trim(a.DocumentNumber), " was already issued for this instruction");
                return;
            }
        }

        SqlioRow row;
        try
        {
            row = await sql.SingleAsync("EXEC core.usp_NextDocumentNumber ?, ?, ?, ?",
                [SqlioParm.Text(company), SqlioParm.Text(series), SqlioParm.Text(K.X(h.User, 20)),
                 CobolText.IsSpaces(correlation) ? SqlioParm.Null : SqlioParm.Text(correlation)],
                [10, 20, 30, 12], ct);
        }
        catch (SqlioException ex)
        {
            K.SqlFailed(h, ex, "NUMGEN", "NUM0008");
            return;
        }
        if (!row.Found)
        {
            // unreachable with the current procedure (BRF-NUMGEN-01)
            h.ReturnCode = 16;
            h.ReasonCode = "NUM0005";
            h.Message = K.Msg("No active number series ", K.Trim(series), " for company ", K.Trim(company));
            return;
        }
        a.DocumentNumber = row.Text(2);
        a.Sequence = (long)CobolNumeric.Store(row.NumVal(3), 18, 0);
        a.Status = "ISSUED";
        h.Message = K.Msg("Reference ", K.Trim(a.DocumentNumber), " issued from series ", K.Trim(series));
    }

    /// <summary>2000-VOID-NUMBER.</summary>
    private async Task VoidNumberAsync(NumgenArea a, string series, CancellationToken ct)
    {
        var h = a.Header;
        var number = K.X(a.DocumentNumber, 30);
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync("EXEC core.usp_VoidDocumentNumber ?, ?, ?, ?, ?",
                [SqlioParm.Text(K.X(h.Company, 10)), SqlioParm.Text(series), SqlioParm.Text(number),
                 SqlioParm.Text(K.X(h.User, 20)), SqlioParm.Text(K.X(a.ReasonText, 200))],
                [10, 20, 30, 12, 200], ct);
        }
        catch (SqlioException ex)
        {
            K.SqlFailed(h, ex, "NUMGEN", "NUM0008");
            return;
        }
        if (!row.Found)
        {
            // unreachable with the current procedure (BRF-NUMGEN-01)
            h.ReturnCode = 16;
            h.ReasonCode = "NUM0006";
            h.Message = K.Msg("Reference ", K.Trim(number), " was never issued from this series");
            return;
        }
        a.Status = row.Text(3);
        h.Message = K.Msg("Reference ", K.Trim(number), " voided and retained - it will not be reissued");
    }
}
