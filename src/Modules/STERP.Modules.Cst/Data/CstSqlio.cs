using System.Collections.ObjectModel;
using System.Data.Common;
using Dapper;
using Sterp.Modules.Cst.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Cst.Data;

/// <summary>
/// The SQL helper sections every CST online program carries an identical copy of (F-COSTROLL-27): 8000-SINGLE-ROW (SNGL),
/// 8100-EXECUTE (EXEC + CLOS), the EXEC + FTCH cursor loop and 8900-SQL-ERROR (WS-SQL-FAILED, CST9001, CA-MESSAGE =
/// SQLIO-ERRMSG, RC 12) over the kernel SQLIO statement semantics on the caller's unit of work. Column slots are X(100)
/// (SQLIO-COL-WIDTH set to 100 in 0100-INITIALISE); parameters are SQLIO varchar text.
/// </summary>
internal sealed class CstSqlio(ISqlRunner sql, CaHeader ca)
{
    private const int SlotWidth = 100;

    /// <summary>WS-SQL-FAILED.</summary>
    public bool SqlFailed { get; set; }

    /// <summary>WS-ROW-FOUND of the last SNGL.</summary>
    public bool RowFound { get; private set; }

    /// <summary>WS-CST-ROW after the last SNGL.</summary>
    public SqlioRow Row { get; private set; } = new(100, []);

    /// <summary>SQLIO-ROWCOUNT of the last EXEC.</summary>
    public int RowCount { get; private set; }

    public ISqlRunner Runner => sql;

    private static int[] Widths(int columns) => Enumerable.Repeat(SlotWidth, Math.Max(columns, 1)).ToArray();

    /// <summary>8000-SINGLE-ROW: SNGL; SQLCODE 0 = row found, 100 = not found, other = 8900-SQL-ERROR.</summary>
    public async Task SingleAsync(string statement, int columns, IReadOnlyList<SqlioParm> parms, CancellationToken ct)
    {
        RowFound = false;
        try
        {
            Row = await sql.SingleAsync(statement, parms, Widths(columns), ct);
            RowFound = Row.Found;
        }
        catch (SqlioException ex)
        {
            SqlError(ex.DriverText);
        }
    }

    /// <summary>8100-EXECUTE: EXEC (then CLOS); a failure is 8900-SQL-ERROR. SQLIO-ROWCOUNT is kept.</summary>
    public async Task ExecuteAsync(string statement, IReadOnlyList<SqlioParm> parms, CancellationToken ct)
    {
        RowCount = 0;
        try
        {
            var r = await sql.ExecAsync(statement, parms, ct);
            RowCount = r.RowCount;
        }
        catch (SqlioException ex)
        {
            SqlError(ex.DriverText);
        }
    }

    /// <summary>
    /// EXEC + FTCH until SQLCODE 100 (the cursor loops of the programs): every row of the first result set, or null after
    /// 8900-SQL-ERROR (an EXEC or FTCH failure).
    /// </summary>
    public async Task<IReadOnlyList<SqlioRow>?> FetchAllAsync(string statement, int columns, IReadOnlyList<SqlioParm> parms, CancellationToken ct)
    {
        try
        {
            return await sql.FetchAllAsync(statement, parms, Widths(columns), ct);
        }
        catch (SqlioException ex)
        {
            SqlError(ex.DriverText);
            return null;
        }
    }

    /// <summary>8900-SQL-ERROR.</summary>
    public void SqlError(string driverText)
    {
        SqlFailed = true;
        ca.SqlError(driverText);
    }
}

/// <summary>Outcome of a streamed SQLIO cursor: whether EXEC failed, whether a FTCH failed after rows, the driver text.</summary>
internal readonly record struct CursorOutcome(bool ExecFailed, bool FetchFailed, string DriverText, int Rows);

/// <summary>Cursor reading for the batch programs (EXEC, FTCH per row with the row processed before the next FTCH).</summary>
internal static class SqlioCursor
{
    /// <summary>
    /// EXEC then FTCH row by row: <paramref name="onRow"/> runs for each fetched row (return false to stop fetching). A
    /// failure before the first row is an EXEC failure; after it, a FTCH failure (the rows already fetched were processed).
    /// </summary>
    public static async Task<CursorOutcome> RunAsync(ISqlRunner sql, string statement, IReadOnlyList<SqlioParm> parms, int columns,
        Func<SqlioRow, Task<bool>> onRow, CancellationToken ct)
    {
        var widths = Enumerable.Repeat(100, Math.Max(columns, 1)).ToArray();
        var rows = 0;
        var callbackFailed = false;
        try
        {
            await sql.WithReaderAsync(Sqlio.Prepare(statement), Sqlio.Bind(parms), async reader =>
            {
                while (reader.FieldCount == 0)
                {
                    if (!await reader.NextResultAsync(ct)) return 0;
                }
                var schema = Schema(reader);
                while (await reader.ReadAsync(ct))
                {
                    rows++;
                    var row = new SqlioRow(0, Slots(reader, schema, widths));
                    bool more;
                    try
                    {
                        more = await onRow(row);
                    }
                    catch
                    {
                        callbackFailed = true;
                        throw;
                    }
                    if (!more) break;
                }
                return 0;
            }, ct);
            return new CursorOutcome(false, false, "", rows);
        }
        catch (SqlioException ex) when (!callbackFailed)
        {
            return rows == 0 ? new CursorOutcome(true, false, ex.DriverText, 0) : new CursorOutcome(false, true, ex.DriverText, rows);
        }
    }

    private static ReadOnlyCollection<DbColumn> Schema(DbDataReader reader)
    {
        var inner = reader is IWrappedDataReader w && w.Reader is DbDataReader r ? r : reader;
        return inner.GetColumnSchema();
    }

    private static string[] Slots(DbDataReader reader, ReadOnlyCollection<DbColumn> schema, IReadOnlyList<int> widths)
    {
        var slots = new string[widths.Count];
        for (var c = 0; c < widths.Count; c++)
        {
            string? text = null;
            if (c < reader.FieldCount && !reader.IsDBNull(c)) text = OdbcText.Render(reader, c, schema);
            slots[c] = text is null ? new string(' ', widths[c]) : CobolText.Fit(text, widths[c]);
        }
        return slots;
    }
}
