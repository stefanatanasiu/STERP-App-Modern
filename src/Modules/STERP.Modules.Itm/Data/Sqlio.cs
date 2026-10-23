using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Itm.Data;

/// <summary>
/// The SQLIO calling pattern of the ITM COBOL programs on the request's engine connection (<see cref="ISqlRunner"/>):
/// statements prepared with positional parameters bound as character data (SQL_VARCHAR, trailing spaces trimmed, at most
/// 256 bytes; NULL only where the program set SQLIO-PARM-NULL), SNGL = execute + first row, EXEC = execute + row count,
/// cursor loops over the buffered result. Driver failures are returned (not thrown) as the SQLIO-ERRMSG text so each
/// program can apply its own 9300-SQL-FAILED rule.
/// </summary>
internal sealed class Sqlio(ISqlRunner runner)
{
    public ISqlRunner Runner => runner;

    public IUnitOfWork UnitOfWork => runner.UnitOfWork;

    /// <summary>SNGL: the first row of the first result set (null = SQLCODE 100).</summary>
    public async Task<SqlioOutcome> SingleAsync(string sql, object?[] args, CancellationToken ct)
    {
        try
        {
            var row = await runner.WithReaderAsync(sql, Bind(args), async r =>
            {
                if (!await r.ReadAsync(ct)) return null;
                return ReadRow(r);
            }, ct);
            return new SqlioOutcome(row is null ? 100 : 0, row is null ? null : [row], 0, "");
        }
        catch (SqlioException ex)
        {
            return SqlioOutcome.Failed(ex);
        }
    }

    /// <summary>EXEC + FTCH loop: every row of the first result set (buffered).</summary>
    public async Task<SqlioOutcome> QueryAsync(string sql, object?[] args, CancellationToken ct)
    {
        try
        {
            var rows = await runner.WithReaderAsync(sql, Bind(args), async r =>
            {
                var list = new List<object?[]>();
                while (await r.ReadAsync(ct)) list.Add(ReadRow(r));
                return list;
            }, ct);
            return new SqlioOutcome(0, rows, rows.Count, "");
        }
        catch (SqlioException ex)
        {
            return SqlioOutcome.Failed(ex);
        }
    }

    /// <summary>EXEC: SQLIO-ROWCOUNT of the statement.</summary>
    public async Task<SqlioOutcome> ExecAsync(string sql, object?[] args, CancellationToken ct)
    {
        try
        {
            var n = await runner.ExecuteAsync(sql, Bind(args), ct);
            return new SqlioOutcome(0, null, n, "");
        }
        catch (SqlioException ex)
        {
            return SqlioOutcome.Failed(ex);
        }
    }

    /// <summary>Positional arguments @p1..@pn: text as ANSI varchar (SQLIO trims and cuts at 256); null = NULL.</summary>
    public static DynamicParameters Bind(object?[] args)
    {
        var p = new DynamicParameters();
        for (var i = 0; i < args.Length; i++)
        {
            var name = "@p" + (i + 1).ToString(CultureInfo.InvariantCulture);
            switch (args[i])
            {
                case null:
                    p.Add(name, null, DbType.AnsiString, size: 256);
                    break;
                case string s:
                    var v = CobolText.Fit(s, 256).TrimEnd(' ');
                    p.Add(name, new DbString { Value = v, IsAnsi = true, Length = 256 });
                    break;
                default:
                    p.Add(name, Convert.ToString(args[i], CultureInfo.InvariantCulture) is { } t
                        ? new DbString { Value = t, IsAnsi = true, Length = 256 }
                        : null);
                    break;
            }
        }
        return p;
    }

    private static object?[] ReadRow(DbDataReader r)
    {
        var row = new object?[r.FieldCount];
        for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
        return row;
    }
}

/// <summary>Result of one SQLIO call: SQLCODE (0, 100, -1), rows, row count and SQLIO-ERRMSG.</summary>
internal sealed record SqlioOutcome(int SqlCode, IReadOnlyList<object?[]>? Rows, int RowCount, string ErrorText)
{
    public bool IsError => SqlCode == -1;

    public bool NotFound => SqlCode == 100;

    public object?[] Row => Rows![0];

    public static SqlioOutcome Failed(SqlioException ex) => new(-1, null, 0, ex.DriverText);

    /// <summary>SQLIO-ERRMSG(1:200) (or the program's fallback text when the driver text is blank).</summary>
    public string Message200(string program) =>
        CobolText.IsSpaces(ErrorText) ? "SQL failure in " + program : Col.Fit(ErrorText, 200);
}

/// <summary>Reads ITM-ROW slots: every column was fetched as text into a 100-byte slot (NULL = spaces).</summary>
internal static class Col
{
    /// <summary>PIC X(n) content of a value, right-trimmed.</summary>
    public static string Fit(string? value, int n) => CobolText.Fit(value, n).TrimEnd(' ');

    /// <summary>ITM-COL(i) as text (slot width 100 unless stated).</summary>
    public static string Text(object?[] row, int index, int width = 100) => Fit(AsText(row[index]), width);

    /// <summary>FUNCTION NUMVAL(ITM-COL(i)) (NULL/spaces = 0).</summary>
    public static decimal Num(object?[] row, int index) => row[index] switch
    {
        null => 0m,
        decimal d => d,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        bool bo => bo ? 1m : 0m,
        double db => (decimal)db,
        float f => (decimal)f,
        string s => NumVal(s),
        var o => NumVal(Convert.ToString(o, CultureInfo.InvariantCulture)),
    };

    /// <summary>ITM-COL(i)(1:1) = "1" for a bit column.</summary>
    public static bool Bit(object?[] row, int index) => AsText(row[index]).StartsWith('1');

    /// <summary>The ODBC SQL_C_CHAR text of a column value.</summary>
    public static string AsText(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "1" : "0",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateTime dt when dt.TimeOfDay == TimeSpan.Zero => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>FUNCTION NUMVAL as the deployed GnuCOBOL 3.2 runtime evaluates it (kernel, SME-039).</summary>
    public static decimal NumVal(string? text) => CobolNumeric.NumVal(text);
}
