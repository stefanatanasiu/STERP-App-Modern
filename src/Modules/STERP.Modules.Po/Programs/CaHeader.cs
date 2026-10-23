using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// STCAHDR (cobol/copy/STCAHDR.cpy) of one Purchasing COMMAREA: the inputs the caller moved in and the outcome the program
/// leaves behind. Programs mutate it in place exactly as the COBOL program updated its LINKAGE header.
/// </summary>
internal sealed class CaHeader
{
    /// <summary>CA-PROGRAM X(8).</summary>
    public string Program { get; set; } = "";
    /// <summary>CA-FUNCTION X(8).</summary>
    public string Function { get; set; } = "";
    /// <summary>CA-COMPANY X(10).</summary>
    public string Company { get; set; } = "";
    /// <summary>CA-SITE X(10).</summary>
    public string Site { get; set; } = "";
    /// <summary>CA-USER X(20).</summary>
    public string User { get; set; } = "";
    /// <summary>CA-BUSINESS-DATE 9(8).</summary>
    public int BusinessDate { get; set; }
    /// <summary>CA-CORRELATION-ID X(36).</summary>
    public string CorrelationId { get; set; } = "";
    /// <summary>CA-RETURN-CODE 9(2).</summary>
    public int ReturnCode { get; set; }
    /// <summary>CA-REASON-CODE X(8).</summary>
    public string ReasonCode { get; set; } = "";
    /// <summary>CA-MESSAGE X(200).</summary>
    public string Message { get; set; } = "";
    /// <summary>CA-REQ-REF X(10).</summary>
    public string ReqRef { get; set; } = "";
    /// <summary>CA-MSG-COUNT 9(2).</summary>
    public int MsgCount { get; set; }

    /// <summary>CA-FAILED (08 thru 16).</summary>
    public bool Failed => ReturnCode is >= 8 and <= 16;

    public LegacyResult Result => new LegacyResult(ReturnCode % 100, ReasonCode, Message, MsgCount % 100, ReqRef).Normalized();

    /// <summary>The header the legacy CobolGateway built (CobolGateway.cs:69-83): function upper-cased, session values.</summary>
    public static CaHeader From(SterpContext s, string function, string? correlationId = null) => new()
    {
        Function = Cob.X(function.ToUpperInvariant(), 8),
        Company = Cob.X(s.CompanyCode, 10),
        Site = Cob.X(s.SiteCode, 10),
        User = Cob.X(s.UserCode, 20),
        BusinessDate = CobolDate.ToYyyymmdd(s.BusinessDate),
        CorrelationId = Cob.X(correlationId ?? s.CorrelationId, 36),
    };

    /// <summary>An INITIALIZEd callee header with the caller's values moved in field by field.</summary>
    public static CaHeader Call(string function, string company, string site, string user, int businessDate, string correlationId) => new()
    {
        Function = Cob.X(function, 8),
        Company = Cob.X(company, 10),
        Site = Cob.X(site, 10),
        User = Cob.X(user, 20),
        BusinessDate = businessDate,
        CorrelationId = Cob.X(correlationId, 36),
    };

    /// <summary>The kernel STCAHDR inputs (company, site, user, business date, correlation id) of this header.</summary>
    public KernelCaller Kernel() => KernelCaller.Empty with
    {
        Company = Company,
        Site = Site,
        User = User,
        BusinessDate = BusinessDate,
        CorrelationId = CorrelationId,
    };

    public void Set(int rc, string reason, string message)
    {
        ReturnCode = rc;
        ReasonCode = reason;
        Message = Cob.X(message, 200);
    }
}

/// <summary>Outcome of one SQLIO call: SQLCODE 0 / 100 / -1, rows, SQLIO-ROWCOUNT and SQLIO-ERRMSG.</summary>
internal readonly record struct Sq(int Code, IReadOnlyList<SqlioRow> Rows, int RowCount, string Err)
{
    public bool Ok => Code == 0;
    public bool NotFound => Code == 100;
    public bool Error => Code == -1;

    /// <summary>The single row of a SNGL (spaces when not found).</summary>
    public SqlioRow Row => Rows.Count > 0 ? Rows[0] : new SqlioRow(100, []);

    /// <summary>SQLIO-ERRMSG(1:200).</summary>
    public string Err200 => Cob.X(Err, 200);
}

/// <summary>
/// The SQLIO calling pattern of the Purchasing COBOL programs on the request's engine connection (<see cref="ISqlRunner"/>):
/// statement text with '?' markers, positional varchar parameters, SNGL / EXEC / OPEN+FTCH, BEGN / COMT / ROLB. Driver
/// failures are returned (SQLCODE -1 with SQLIO-ERRMSG) so each program applies its own failure rule.
/// </summary>
internal sealed class PoSql(ISqlRunner runner)
{
    public ISqlRunner Runner => runner;

    public IUnitOfWork UnitOfWork => runner.UnitOfWork;

    public async Task<Sq> Sngl(string stmt, SqlioParm[] parms, int[] widths, CancellationToken ct)
    {
        try
        {
            var row = await runner.SingleAsync(stmt, parms, widths, ct);
            return new Sq(row.SqlCode, row.Found ? [row] : [], row.Found ? 1 : 0, "");
        }
        catch (SqlioException ex)
        {
            return new Sq(-1, [], 0, ex.DriverText);
        }
    }

    /// <summary>EXEC + FTCH until end of data (the buffered cursor).</summary>
    public async Task<Sq> Fetch(string stmt, SqlioParm[] parms, int[] widths, CancellationToken ct)
    {
        try
        {
            var rows = await runner.FetchAllAsync(stmt, parms, widths, ct);
            return new Sq(0, rows, rows.Count, "");
        }
        catch (SqlioException ex)
        {
            return new Sq(-1, [], 0, ex.DriverText);
        }
    }

    /// <summary>
    /// EXEC, then FTCH until SQLCODE 100 as the batch loops ran it ("IF SQLIO-NOTFOUND OR SQLIO-ERROR" ends the fetch either
    /// way). SQLExecute (sterpsql.c STSQLEXE) returns once the first row, the end or an error is in, so a failure before the
    /// first row is the EXEC's (SQLCODE -1 with SQLIO-ERRMSG; e.g. PODOC's appendix union, STERP-W-1746, verified on the legacy
    /// oracle); a failure at a later FTCH (SQLFetch, STSQLFET) ends the loop and keeps the rows fetched before it.
    /// </summary>
    public async Task<Sq> Cursor(string stmt, SqlioParm[] parms, int[] widths, CancellationToken ct)
    {
        try
        {
            var rows = await runner.WithReaderAsync<IReadOnlyList<SqlioRow>>(Sqlio.Prepare(stmt), Sqlio.Bind(parms), async reader =>
            {
                var list = new List<SqlioRow>();
                var more = reader.FieldCount > 0 && await reader.ReadAsync(ct);
                try
                {
                    if (reader.FieldCount == 0)
                    {
                        while (reader.FieldCount == 0)
                        {
                            if (!await reader.NextResultAsync(ct)) return list;
                        }
                        more = await reader.ReadAsync(ct);
                    }
                    var inner = reader is Dapper.IWrappedDataReader w && w.Reader is System.Data.Common.DbDataReader d ? d : reader;
                    var schema = System.Data.Common.DbDataReaderExtensions.GetColumnSchema(inner);
                    while (more)
                    {
                        var slots = new string[widths.Length];
                        for (var c = 0; c < widths.Length; c++)
                        {
                            string? text = null;
                            if (c < reader.FieldCount && !reader.IsDBNull(c)) text = OdbcText.Render(reader, c, schema);
                            slots[c] = text is null ? new string(' ', widths[c]) : Sterp.Platform.Kernel.Cobol.CobolText.Fit(text, widths[c]);
                        }
                        list.Add(new SqlioRow(0, slots));
                        more = await reader.ReadAsync(ct);
                    }
                }
                catch (Microsoft.Data.SqlClient.SqlException)
                {
                    // STSQLFET -1: the program's loop takes it as the end of the rows
                }
                return list;
            }, ct);
            return new Sq(0, rows, rows.Count, "");
        }
        catch (SqlioException ex)
        {
            return new Sq(-1, [], 0, ex.DriverText);
        }
    }

    /// <summary>
    /// EXEC without fetch. SQLCODE 100 (SQL_NO_DATA) only when the statement produced no result set and the server reported
    /// a row count of zero; a procedure under SET NOCOUNT ON reports no count at all and executes with SQL_SUCCESS (0), as
    /// SQLExecute did for the legacy EXEC po.usp_ReceiptDeliveryState.
    /// </summary>
    public async Task<Sq> Exec(string stmt, SqlioParm[] parms, CancellationToken ct)
    {
        try
        {
            var (code, rows) = await runner.WithReaderAsync(Sqlio.Prepare(stmt), Sqlio.Bind(parms), async reader =>
            {
                var sawRowset = false;
                do
                {
                    if (reader.FieldCount > 0)
                    {
                        sawRowset = true;
                        while (await reader.ReadAsync(ct))
                        {
                        }
                    }
                } while (await reader.NextResultAsync(ct));
                var affected = reader.RecordsAffected;
                return (!sawRowset && affected == 0 ? 100 : 0, Math.Max(affected, 0));
            }, ct);
            return new Sq(code, [], rows, "");
        }
        catch (SqlioException ex)
        {
            return new Sq(-1, [], 0, ex.DriverText);
        }
    }

    public static SqlioParm Tx(string? v) => SqlioParm.Text(v);

    public static SqlioParm Num(decimal v) => SqlioParm.Number(v);

    public static SqlioParm Int(long v) => SqlioParm.Integer(v);

    public static SqlioParm D8(int yyyymmdd) => SqlioParm.Text(Cob.D8(yyyymmdd));

    public static SqlioParm Null => SqlioParm.Null;
}
