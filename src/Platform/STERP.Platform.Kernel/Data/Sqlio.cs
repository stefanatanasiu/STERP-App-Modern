using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Dapper;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Platform.Kernel.Data;

/// <summary>
/// One SQLIO positional parameter (SQLIO-PARM-VAL / SQLIO-PARM-NULL, SQLIOCA.cpy): bound as varchar text with trailing
/// spaces and low-values removed and at most 256 characters (sterpsql.c cobstr / STSQLPRM), or NULL when the legacy
/// program set the null flag. A blank value that is not flagged NULL reaches SQL Server as '' (empty string).
/// </summary>
public readonly record struct SqlioParm(string? Value)
{
    /// <summary>SQLIO-PARM-NULL = "Y".</summary>
    public static SqlioParm Null => new(null);

    /// <summary>MOVE &lt;alphanumeric&gt; TO SQLIO-PARM-VAL (null is treated as SPACES, not as NULL).</summary>
    public static SqlioParm Text(string? value) => new(CobolText.Fit(value, 256).TrimEnd(' ', '\0'));

    /// <summary>The value, or NULL when it is SPACES (the "IF field = SPACES MOVE 'Y' TO SQLIO-PARM-NULL" pattern).</summary>
    public static SqlioParm NullIfSpaces(string? value) => CobolText.IsSpaces(CobolText.Fit(value, 256)) ? Null : Text(value);

    /// <summary>MOVE number TO SQLIO-NUM-EDIT, MOVE FUNCTION TRIM(SQLIO-NUM-EDIT) TO SQLIO-PARM-VAL (-(12)9.9(6)).</summary>
    public static SqlioParm Number(decimal value) => new(CobolNumeric.SqlioNumber(value));

    /// <summary>MOVE integer TO SQLIO-INT-EDIT, MOVE FUNCTION TRIM(SQLIO-INT-EDIT) TO SQLIO-PARM-VAL (-(17)9).</summary>
    public static SqlioParm Integer(long value) => new(CobolNumeric.SqlioInteger(value));

    /// <summary>MOVE PIC 9(width) field TO SQLIO-PARM-VAL: the zero-padded digits (a yyyymmdd date is Digits(date, 8)).</summary>
    public static SqlioParm Digits(long value, int width) =>
        new(Math.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')[^width..]);

    public bool IsNull => Value is null;
}

/// <summary>SQLIO SNGL / FTCH answer: SQLCODE 0 with the row slots, or 100 (no row).</summary>
public sealed class SqlioRow
{
    public SqlioRow(int sqlCode, IReadOnlyList<string> slots)
    {
        SqlCode = sqlCode;
        Slots = slots;
    }

    /// <summary>0 = a row was fetched, 100 = not found / end of data.</summary>
    public int SqlCode { get; }

    public bool Found => SqlCode == 0;

    /// <summary>Column slots exactly as STSQLFET filled the row area: left justified, truncated or space padded to the width.</summary>
    public IReadOnlyList<string> Slots { get; }

    /// <summary>Slot n (0-based), right-trimmed.</summary>
    public string Text(int n) => n < Slots.Count ? Slots[n].TrimEnd(' ') : "";

    /// <summary>Slot n as stored (space padded to its width).</summary>
    public string Raw(int n) => n < Slots.Count ? Slots[n] : "";

    /// <summary>FUNCTION NUMVAL(slot n).</summary>
    public decimal NumVal(int n) => CobolNumeric.NumVal(Raw(n));
}

/// <summary>SQLIO EXEC answer: SQLCODE 0, or 100 when the driver reported no data (a searched INSERT/UPDATE/DELETE that touched no row), and SQLIO-ROWCOUNT.</summary>
public readonly record struct SqlioExecResult(int SqlCode, int RowCount);

/// <summary>
/// The SQLIO statement functions on the request's engine connection and unit of work (FR-STERP-PLATFORM-011): legacy
/// statement text with '?' markers, positional varchar parameters, column values fetched as ODBC character text into
/// fixed-width slots (NULL as spaces). Failures are raised as <see cref="SqlioException"/> (SQLCODE -1) with the legacy
/// driver text; callers classify them exactly as their COBOL original did.
/// </summary>
public static class Sqlio
{
    /// <summary>SNGL: EXEC + FTCH of the first row of the first result set that has columns + CLOS.</summary>
    public static Task<SqlioRow> SingleAsync(this ISqlRunner sql, string statement, IReadOnlyList<SqlioParm> parms, IReadOnlyList<int> widths, CancellationToken ct = default) =>
        sql.WithReaderAsync(Prepare(statement), Bind(parms), async reader =>
        {
            if (!await MoveToRowsetAsync(reader, ct) || !await reader.ReadAsync(ct)) return NotFound(widths);
            return new SqlioRow(0, Slots(reader, Schema(reader), widths));
        }, ct);

    /// <summary>EXEC + FTCH until SQLCODE 100: every row of the first result set that has columns, as slots.</summary>
    public static Task<IReadOnlyList<SqlioRow>> FetchAllAsync(this ISqlRunner sql, string statement, IReadOnlyList<SqlioParm> parms, IReadOnlyList<int> widths, CancellationToken ct = default) =>
        sql.WithReaderAsync<IReadOnlyList<SqlioRow>>(Prepare(statement), Bind(parms), async reader =>
        {
            var rows = new List<SqlioRow>();
            if (!await MoveToRowsetAsync(reader, ct)) return rows;
            var schema = Schema(reader);
            while (await reader.ReadAsync(ct)) rows.Add(new SqlioRow(0, Slots(reader, schema, widths)));
            return rows;
        }, ct);

    /// <summary>
    /// EXEC + FTCH, then MORE + FTCH for each further result set (PERCLOSE reads the period header set, then the checklist
    /// steps): the rows of every result set that has columns, in order, each fetched with its own slot widths; sets beyond
    /// <paramref name="widthsPerSet"/> are not read. A failure in any set raises <see cref="SqlioException"/> for the call
    /// (use <see cref="ISqlRunner.WithReaderAsync{T}"/> where the legacy program kept the rows fetched before a MORE failure).
    /// </summary>
    public static Task<IReadOnlyList<IReadOnlyList<SqlioRow>>> FetchSetsAsync(this ISqlRunner sql, string statement, IReadOnlyList<SqlioParm> parms,
        IReadOnlyList<IReadOnlyList<int>> widthsPerSet, CancellationToken ct = default) =>
        sql.WithReaderAsync<IReadOnlyList<IReadOnlyList<SqlioRow>>>(Prepare(statement), Bind(parms), async reader =>
        {
            var sets = new List<IReadOnlyList<SqlioRow>>();
            while (sets.Count < widthsPerSet.Count && await MoveToRowsetAsync(reader, ct))
            {
                var widths = widthsPerSet[sets.Count];
                var schema = Schema(reader);
                var rows = new List<SqlioRow>();
                while (await reader.ReadAsync(ct)) rows.Add(new SqlioRow(0, Slots(reader, schema, widths)));
                sets.Add(rows);
                if (!await reader.NextResultAsync(ct)) break;
            }
            return sets;
        }, ct);

    /// <summary>
    /// EXEC without fetch: SQLCODE 100 only when the statement produced no result set and reported a row count of 0 (ODBC
    /// SQL_NO_DATA for a searched UPDATE/DELETE that touched no row); otherwise 0. A statement that reports no count at all
    /// (SET NOCOUNT ON procedures) is 0, as the legacy SQLIO EXEC returned (PO RECEIPT on po.usp_ReceiptDeliveryState).
    /// </summary>
    public static Task<SqlioExecResult> ExecAsync(this ISqlRunner sql, string statement, IReadOnlyList<SqlioParm> parms, CancellationToken ct = default) =>
        sql.WithReaderAsync(Prepare(statement), Bind(parms), async reader =>
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
            var rows = reader.RecordsAffected;
            return new SqlioExecResult(sawRowset || rows != 0 ? 0 : 100, Math.Max(rows, 0));
        }, ct);

    /// <summary>Replaces the '?' parameter markers outside literals, quoted identifiers and comments by @p1..@pn.</summary>
    public static string Prepare(string statement)
    {
        var sb = new StringBuilder(statement.Length + 16);
        var n = 0;
        for (var i = 0; i < statement.Length; i++)
        {
            var c = statement[i];
            switch (c)
            {
                case '\'':
                case '"':
                case '[':
                {
                    var close = c == '[' ? ']' : c;
                    sb.Append(c);
                    i++;
                    while (i < statement.Length)
                    {
                        sb.Append(statement[i]);
                        if (statement[i] == close)
                        {
                            if (i + 1 < statement.Length && statement[i + 1] == close)
                            {
                                sb.Append(statement[++i]);
                            }
                            else
                            {
                                break;
                            }
                        }
                        i++;
                    }
                    break;
                }
                case '-' when i + 1 < statement.Length && statement[i + 1] == '-':
                    while (i < statement.Length && statement[i] != '\n') sb.Append(statement[i++]);
                    if (i < statement.Length) sb.Append(statement[i]);
                    break;
                case '/' when i + 1 < statement.Length && statement[i + 1] == '*':
                {
                    var end = statement.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? statement.Length : end + 2;
                    sb.Append(statement, i, end - i);
                    i = end - 1;
                    break;
                }
                case '?':
                    sb.Append("@p").Append(++n);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The parameters as varchar(len) values named @p1..@pn (STSQLPRM: SQL_VARCHAR, column size = text length, at least 1).</summary>
    public static DynamicParameters Bind(IReadOnlyList<SqlioParm> parms)
    {
        var dp = new DynamicParameters();
        for (var i = 0; i < parms.Count; i++)
        {
            var v = parms[i].Value;
            dp.Add("@p" + (i + 1).ToString(CultureInfo.InvariantCulture), v is null ? DBNull.Value : v, DbType.AnsiString,
                ParameterDirection.Input, Math.Max(1, v?.Length ?? 1));
        }
        return dp;
    }

    private static SqlioRow NotFound(IReadOnlyList<int> widths) => new(100, widths.Select(w => new string(' ', w)).ToArray());

    /// <summary>STSQLFET skips row-count-only results so the caller lands on the first real result set.</summary>
    private static async Task<bool> MoveToRowsetAsync(DbDataReader reader, CancellationToken ct)
    {
        while (reader.FieldCount == 0)
        {
            if (!await reader.NextResultAsync(ct)) return false;
        }
        return true;
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

/// <summary>
/// The text the ODBC Driver 18 character conversion (SQLGetData SQL_C_CHAR) produced for a column, as the COBOL row area
/// received it: numbers without leading zero before the decimal point (.50), the declared scale kept, bit as 1/0,
/// date yyyy-mm-dd, datetime with 3 and datetime2(n) with n fractional digits, Unicode text in the ANSI code page.
/// </summary>
public static class OdbcText
{
    public static string Render(DbDataReader reader, int ordinal, ReadOnlyCollection<DbColumn>? schema = null)
    {
        var typeName = reader.GetDataTypeName(ordinal).ToLowerInvariant();
        var col = schema is not null && ordinal < schema.Count ? schema[ordinal] : null;
        var value = reader.GetValue(ordinal);
        switch (value)
        {
            case string s:
                return CobolText.Ansi.GetString(CobolText.Ansi.GetBytes(s));
            case bool b:
                return b ? "1" : "0";
            case byte or short or int or long:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case decimal d:
            {
                var scale = typeName is "money" or "smallmoney" ? 4 : col?.NumericScale ?? DecimalScale(d);
                return Number(d, scale);
            }
            case double db:
                return db.ToString("R", CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case DateTime dt:
                return typeName switch
                {
                    "date" => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "smalldatetime" => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    "datetime" => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    _ => Fraction(dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture), col?.NumericScale ?? 7),
                };
            case DateTimeOffset dto:
            {
                var text = Fraction(dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture), col?.NumericScale ?? 7);
                return text + " " + dto.ToString("zzz", CultureInfo.InvariantCulture);
            }
            case TimeSpan ts:
                return Fraction(ts.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture), col?.NumericScale ?? 7);
            case Guid g:
                return g.ToString("D").ToUpperInvariant();
            case byte[] bytes:
                return Convert.ToHexString(bytes);
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    /// <summary>A numeric value in the SQL Server ODBC character form: sign, integer digits (none when zero), point, scale digits.</summary>
    public static string Number(decimal value, int scale)
    {
        var s = Math.Abs(value).ToString("F" + scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (scale > 0 && s.StartsWith("0.", StringComparison.Ordinal)) s = s[1..];
        return value < 0 ? "-" + s : s;
    }

    private static int DecimalScale(decimal d) => (decimal.GetBits(d)[3] >> 16) & 0xFF;

    private static string Fraction(string full, int digits)
    {
        var dot = full.LastIndexOf('.');
        if (dot < 0) return full;
        return digits <= 0 ? full[..dot] : full[..Math.Min(full.Length, dot + 1 + digits)];
    }
}
