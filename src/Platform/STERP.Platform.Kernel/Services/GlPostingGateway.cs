using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>
/// One fin.JournalStage row as the caller's legacy INSERT staged it (for example POPOST.cob:181-215): CorrelationId and
/// LineNum first, then the caller's columns in the caller's order, each bound as SQLIO text - plain (a blank value reaches
/// SQL Server as ''), wrapped in NULLIF(?,'') where the legacy statement did, or NULL.
/// </summary>
public sealed class JournalStageLine
{
    private static readonly HashSet<string> Columns = new(StringComparer.Ordinal)
    {
        "CompanyCode", "AccountCode", "DebitAmount", "CreditAmount", "CurrencyCode", "DebitAmountGBP", "CreditAmountGBP",
        "DimCostCentre", "DimProject", "Narrative", "VatCode", "JobRef", "SubledgerType", "SubledgerRef", "SourceRef",
        "BankAccountCode", "CreatedBy",
    };

    private readonly List<(string Column, SqlioParm Value, bool NullIf)> _values = [];

    public JournalStageLine(string correlationId, int lineNum)
    {
        CorrelationId = correlationId;
        LineNum = lineNum;
    }

    public string CorrelationId { get; }

    public int LineNum { get; }

    /// <summary>
    /// Stage as INSERT ... SELECT ... WHERE NOT EXISTS (same CorrelationId and LineNum), the re-runnable form of BANKREC,
    /// FXREVAL, GLADJUST and YEAREND (a repeated line is SQLCODE 100, not a failure).
    /// </summary>
    public bool IfNotExists { get; init; }

    public IReadOnlyList<(string Column, SqlioParm Value, bool NullIf)> Values => _values;

    /// <summary>A text column bound as the legacy MOVE left it (blank is '', not NULL).</summary>
    public JournalStageLine Text(string column, string? value) => Add(column, SqlioParm.Text(value), false);

    /// <summary>A column the legacy statement wrapped in NULLIF(?,'').</summary>
    public JournalStageLine NullIfBlank(string column, string? value) => Add(column, SqlioParm.Text(value), true);

    /// <summary>An amount through SQLIO-NUM-EDIT -(12)9.9(6) (decimal(18,2) is reached by SQL Server's conversion of that text).</summary>
    public JournalStageLine Amount(string column, decimal value) => Add(column, SqlioParm.Number(value), false);

    /// <summary>A column bound as SQL NULL.</summary>
    public JournalStageLine Null(string column) => Add(column, SqlioParm.Null, false);

    /// <summary>A column bound with an explicit SQLIO parameter.</summary>
    public JournalStageLine Parm(string column, SqlioParm value, bool nullIfBlank = false) => Add(column, value, nullIfBlank);

    private JournalStageLine Add(string column, SqlioParm value, bool nullIf)
    {
        if (!Columns.Contains(column)) throw new ArgumentException($"fin.JournalStage has no stageable column {column}.", nameof(column));
        _values.Add((column, value, nullIf));
        return this;
    }
}

/// <summary>An EXEC answer of the gateway: SQLCODE 0 / 100 / -1, the row count and, on -1, the legacy driver text.</summary>
public sealed record GlStageResult(int SqlCode, int RowCount, string ErrorText, SqlioException? Error)
{
    public bool Failed => SqlCode == -1;
}

/// <summary>
/// The caller's fin.usp_PostJournal header values in procedure parameter order. Strings are bound as SQLIO text
/// (null = NULL, blank = ''), dates and integers through SQLIO-INT-EDIT, the rate through SQLIO-NUM-EDIT, exactly as the
/// legacy callers moved them. <see cref="ArgumentCount"/> is the number of positional arguments the caller passed
/// (DESPATCH 12, most callers 14, KITCOMPL 15); later parameters take the procedure defaults.
/// </summary>
public sealed class GlPostRequest
{
    public required string CompanyCode { get; init; }
    public required string CorrelationId { get; init; }
    public required string UserCode { get; init; }
    public required string Description { get; init; }

    /// <summary>yyyymmdd.</summary>
    public required int PostingDate { get; init; }

    public string? JournalType { get; init; } = "GL";

    /// <summary>yyyymmdd or null (NULL: the procedure takes the posting date).</summary>
    public int? DocumentDate { get; init; }

    public string? CurrencyCode { get; init; } = "GBP";
    public string? SourceModule { get; init; } = "FIN";
    public string? SourceRef { get; init; }
    public string? EventCode { get; init; }
    public string? SeriesCode { get; init; } = "JNL";
    public decimal? FxRate { get; init; }
    public string? EvidenceRef { get; init; }

    /// <summary>@SuppressResult (only where the caller set it, for example KITCOMPL 1).</summary>
    public bool? SuppressResult { get; init; }

    /// <summary>Positional arguments passed (1..15).</summary>
    public int ArgumentCount { get; init; } = 14;

    internal SqlioParm[] Parms()
    {
        static SqlioParm S(string? v) => v is null ? SqlioParm.Null : SqlioParm.Text(v);
        SqlioParm[] all =
        [
            S(CompanyCode), S(CorrelationId), S(UserCode), S(Description), SqlioParm.Integer(PostingDate), S(JournalType),
            DocumentDate is { } d ? SqlioParm.Integer(d) : SqlioParm.Null, S(CurrencyCode), S(SourceModule), S(SourceRef),
            S(EventCode), S(SeriesCode), FxRate is { } r ? SqlioParm.Number(r) : SqlioParm.Null, S(EvidenceRef),
            SuppressResult is { } b ? SqlioParm.Text(b ? "1" : "0") : SqlioParm.Null,
        ];
        return all[..Math.Clamp(ArgumentCount, 1, all.Length)];
    }
}

/// <summary>fin.usp_PostJournal's result row (or no row).</summary>
public sealed record GlPostResult(int SqlCode, string ErrorText, SqlioException? Error, SqlioRow? Row)
{
    /// <summary>SQLIO-ERROR: the procedure (or the driver) refused; <see cref="ErrorText"/> carries STERP-E-0009..0017 / 0004.</summary>
    public bool Failed => SqlCode == -1;

    /// <summary>SQLIO-NOTFOUND: no result row (for example @SuppressResult = 1).</summary>
    public bool NoRow => SqlCode == 100;

    public long JournalId => Row is null ? 0 : (long)CobolNumeric.Store(Row.NumVal(0), 18, 0);
    public string JournalNumber => Row?.Text(1) ?? "";
    public string CompanyCode => Row?.Text(2) ?? "";
    public int FiscalYear => Row is null ? 0 : (int)CobolNumeric.Store(Row.NumVal(3), 9, 0);
    public int PeriodNo => Row is null ? 0 : (int)CobolNumeric.Store(Row.NumVal(4), 9, 0);
    public decimal TotalDebitGbp => Row?.NumVal(5) ?? 0;
    public decimal TotalCreditGbp => Row?.NumVal(6) ?? 0;
    public string Status => Row?.Text(7) ?? "";

    /// <summary>WasDuplicate rendered "1" (a repeated correlation id returned the original journal).</summary>
    public bool WasDuplicate => Row is not null && Row.Raw(8).StartsWith('1');
}

/// <summary>
/// The shared posting contract of the 17 staging and 10 posting programs (FR-STERP-PLATFORM-029): fin.JournalStage
/// inserts and fin.usp_PostJournal on the caller's engine connection and unit of work. The procedure owns its transaction
/// only when none is open, otherwise it joins the caller's unit; a repeated correlation id returns the original journal
/// with WasDuplicate = 1. The gateway adds no retry, validation or reason code of its own: failures come back with the
/// legacy driver text for the caller to map to its own reason codes (for example POP0005 RC 08, POP0006 RC 12 on no row).
/// </summary>
public interface IGlPostingGateway
{
    /// <summary>DELETE fin.JournalStage WHERE CorrelationId = ? (the clear-before-staging statement of APPOST, ARPOSTGL, DESPATCH ...).</summary>
    Task<GlStageResult> ClearStageAsync(string correlationId, CancellationToken ct = default);

    /// <summary>One fin.JournalStage row per call with the caller's column list and NULLIF conversions.</summary>
    Task<GlStageResult> StageLineAsync(JournalStageLine line, CancellationToken ct = default);

    /// <summary>
    /// A caller's own set-based staging statement (OPENBAL, KITCOMPL, FINDEMO stage through INSERT fin.JournalStage ... SELECT);
    /// the statement must be an INSERT into fin.JournalStage with '?' markers.
    /// </summary>
    Task<GlStageResult> StageStatementAsync(string statement, IReadOnlyList<SqlioParm> parms, CancellationToken ct = default);

    /// <summary>EXEC fin.usp_PostJournal with the caller's header values (SQLIO SNGL).</summary>
    Task<GlPostResult> PostAsync(GlPostRequest request, IReadOnlyList<int>? widths = null, CancellationToken ct = default);
}

public sealed class GlPostingGateway(ISqlRunner sql) : IGlPostingGateway
{
    /// <summary>POPOST's result slot widths (POPOST.cob:254-262).</summary>
    public static readonly int[] DefaultWidths = [24, 30, 10, 8, 8, 24, 24, 12, 8];

    public Task<GlStageResult> ClearStageAsync(string correlationId, CancellationToken ct = default) =>
        ExecAsync("DELETE fin.JournalStage WHERE CorrelationId = ?", [SqlioParm.Text(correlationId)], ct);

    public Task<GlStageResult> StageLineAsync(JournalStageLine line, CancellationToken ct = default)
    {
        var columns = new List<string> { "CorrelationId", "LineNum" };
        var markers = new List<string> { "?", "?" };
        var parms = new List<SqlioParm> { SqlioParm.Text(line.CorrelationId), SqlioParm.Integer(line.LineNum) };
        foreach (var (column, value, nullIf) in line.Values)
        {
            columns.Add(column);
            markers.Add(nullIf ? "NULLIF(?,'')" : "?");
            parms.Add(value);
        }
        string statement;
        if (line.IfNotExists)
        {
            statement = "INSERT fin.JournalStage (" + string.Join(", ", columns) + ") SELECT " + string.Join(", ", markers) +
                        " WHERE NOT EXISTS (SELECT 1 FROM fin.JournalStage s WHERE s.CorrelationId = ? AND s.LineNum = ?)";
            parms.Add(SqlioParm.Text(line.CorrelationId));
            parms.Add(SqlioParm.Integer(line.LineNum));
        }
        else
        {
            statement = "INSERT fin.JournalStage (" + string.Join(", ", columns) + ") VALUES (" + string.Join(", ", markers) + ")";
        }
        return ExecAsync(statement, parms, ct);
    }

    public Task<GlStageResult> StageStatementAsync(string statement, IReadOnlyList<SqlioParm> parms, CancellationToken ct = default)
    {
        var s = statement.TrimStart();
        if (!s.StartsWith("INSERT fin.JournalStage", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("INSERT INTO fin.JournalStage", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The gateway stages only INSERT fin.JournalStage statements.", nameof(statement));
        }
        return ExecAsync(statement, parms, ct);
    }

    public async Task<GlPostResult> PostAsync(GlPostRequest request, IReadOnlyList<int>? widths = null, CancellationToken ct = default)
    {
        var parms = request.Parms();
        var statement = "EXEC fin.usp_PostJournal " + string.Join(", ", Enumerable.Repeat("?", parms.Length));
        try
        {
            var row = await sql.SingleAsync(statement, parms, widths ?? DefaultWidths, ct);
            return new GlPostResult(row.SqlCode, "", null, row.Found ? row : null);
        }
        catch (SqlioException ex)
        {
            return new GlPostResult(-1, ex.DriverText, ex, null);
        }
    }

    private async Task<GlStageResult> ExecAsync(string statement, IReadOnlyList<SqlioParm> parms, CancellationToken ct)
    {
        try
        {
            var r = await sql.ExecAsync(statement, parms, ct);
            return new GlStageResult(r.SqlCode, r.RowCount, "", null);
        }
        catch (SqlioException ex)
        {
            return new GlStageResult(-1, 0, ex.DriverText, ex);
        }
    }
}
