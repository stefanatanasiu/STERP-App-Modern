using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Sterp.Platform.Kernel.Data;

/// <summary>Parameter conversion rules of the two legacy session families (contracts 4.4, 6.4).</summary>
public static class SqlParameters
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

    /// <summary>SqlHelper.Params: null and empty string become DBNull (dotnet/STERP.Data/SqlHelper.cs:21-37).</summary>
    public static object? SqlHelperValue(object? v) => v switch
    {
        null => DBNull.Value,
        string s when s.Length == 0 => DBNull.Value,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => v,
    };

    /// <summary>
    /// SQLIO binding: trailing spaces trimmed, at most 256 characters, bound as varchar like the ODBC shim's SQL_VARCHAR
    /// (sterpsql.c STSQLPRM; SQLIO.cob:2100-BIND-ONE) - not Dapper's default nvarchar.
    /// </summary>
    public static object? SqlioValue(object? v) => v switch
    {
        null => DBNull.Value,
        string s => new DbString { Value = Clip(s.TrimEnd(' ', '\0'), 256), IsAnsi = true },
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => v,
    };

    private static string Clip(string s, int n) => s.Length > n ? s[..n] : s;

    public static DynamicParameters? Build(object? param, Func<object?, object?> convert)
    {
        switch (param)
        {
            case null:
                return null;
            case DynamicParameters dp:
                return dp;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
            {
                var d = new DynamicParameters();
                foreach (var (k, v) in pairs) d.Add(k.StartsWith('@') ? k : "@" + k, ForDapper(convert(v)));
                return d;
            }
            default:
            {
                var d = new DynamicParameters();
                foreach (var p in Props.GetOrAdd(param.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
                {
                    d.Add("@" + p.Name, ForDapper(convert(p.GetValue(param))));
                }
                return d;
            }
        }
    }

    /// <summary>Dapper binds a null value as DBNull itself and refuses a DBNull value (no type mapping for System.DBNull).</summary>
    private static object? ForDapper(object? value) => value is DBNull ? null : value;
}

/// <summary>Converts result rows to JSON with the contracts 4.3 representation rules.</summary>
public static class SqlJson
{
    public static readonly JsonNamingPolicy Naming = JsonNamingPolicy.CamelCase;

    public static async Task<JsonArray> ReadRowsAsync(System.Data.Common.DbDataReader reader, CancellationToken ct = default)
    {
        var rows = new JsonArray();
        var names = new string[reader.FieldCount];
        for (var i = 0; i < names.Length; i++)
        {
            var n = reader.GetName(i);
            var type = reader.GetDataTypeName(i);
            // contracts 4.3: a rowversion column is exposed as rowVersion (opaque base64)
            names[i] = type.Equals("timestamp", StringComparison.OrdinalIgnoreCase) || type.Equals("rowversion", StringComparison.OrdinalIgnoreCase)
                ? "rowVersion"
                : string.IsNullOrEmpty(n) ? $"column{i + 1}" : Naming.ConvertName(n);
        }
        while (await reader.ReadAsync(ct))
        {
            var row = new JsonObject();
            for (var i = 0; i < names.Length; i++)
            {
                row[names[i]] = reader.IsDBNull(i) ? null : ToNode(reader.GetDataTypeName(i), reader.GetValue(i));
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>One column value as JSON (contracts 4.3).</summary>
    public static JsonNode? ToNode(string sqlType, object value)
    {
        var t = sqlType.ToLowerInvariant();
        return value switch
        {
            null or DBNull => null,
            string s => JsonValue.Create(s.TrimEnd(' ')),
            decimal d => JsonValue.Create(d),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            short sh => JsonValue.Create((int)sh),
            byte b => JsonValue.Create((int)b),
            bool bo => JsonValue.Create(bo),
            double db => JsonValue.Create(db),
            float f => JsonValue.Create((double)f),
            DateTime dt when t == "date" => JsonValue.Create(dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            DateTime dt => JsonValue.Create(LocalDateTime(dt)),
            DateTimeOffset dto => JsonValue.Create(dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)),
            TimeSpan ts => JsonValue.Create(ts.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture)),
            byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
            Guid g => JsonValue.Create(g.ToString()),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>x-sterp-local-date-time: yyyy-MM-ddTHH:mm:ss.fffffff, no offset (contracts 4.3).</summary>
    public static string LocalDateTime(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    public static string? Date(DateTime? dt) => dt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// The COBOL engine unit of work (SQLIO + c/sterpsql.c): one logical connection per API request or job-step execution,
/// autocommit unless BEGN was issued; BEGN switches to manual commit, COMT/ROLB end the single unit (no nesting: a callee's
/// COMT commits the caller's open work, SME-013); TSTA reports 1 (autocommit) or 0 (contracts 6.4; sterpsql.c:256-283).
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    Task<SqlConnection> ConnectionAsync(CancellationToken ct = default);

    SqlTransaction? Transaction { get; }

    bool InTransaction { get; }

    /// <summary>BEGN: autocommit off (idempotent - SQL_ATTR_AUTOCOMMIT is a switch, not a counter).</summary>
    Task BeginAsync(CancellationToken ct = default);

    /// <summary>COMT: commit the open unit (if any) and restore autocommit.</summary>
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>ROLB: roll back the open unit (if any) and restore autocommit.</summary>
    Task RollbackAsync(CancellationToken ct = default);

    /// <summary>TSTA: 1 = autocommit, 0 = manual commit.</summary>
    int TransactionState { get; }
}

public sealed class EngineUnitOfWork(ISqlConnectionFactory factory) : IUnitOfWork
{
    private SqlConnection? _connection;
    private SqlTransaction? _transaction;
    private bool _manualCommit;

    /// <summary>
    /// The open unit. In manual-commit mode (after BEGN) a unit that the server has already ended - a THROW under
    /// SET XACT_ABORT ON rolls the whole transaction back - is replaced by a new one, exactly as the ODBC connection
    /// left in manual-commit mode starts a new transaction with its next statement.
    /// </summary>
    public SqlTransaction? Transaction
    {
        get
        {
            if (_manualCommit && _connection is not null && (_transaction is null || _transaction.Connection is null))
            {
                _transaction?.Dispose();
                _transaction = _connection.BeginTransaction(IsolationLevel.ReadCommitted);
            }
            return _transaction;
        }
    }

    public bool InTransaction => _manualCommit;

    public int TransactionState => _manualCommit ? 0 : 1;

    /// <summary>sterpsql.c ensure_conn: connection attempts before SQLCODE -1.</summary>
    public const int LoginAttempts = 4;

    /// <summary>sterpsql.c ensure_conn: SQL_ATTR_LOGIN_TIMEOUT 30 seconds.</summary>
    public const int LoginTimeoutSeconds = 30;

    /// <summary>The wait after a failed login attempt (2,000 ms x attempt); replaceable by tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// Opens the engine connection with the login retry of sterpsql.c ensure_conn (FR-STERP-PLATFORM-011): up to four
    /// attempts with a 30 s login timeout, waiting 2,000 ms x attempt after every failed attempt (the last included), no
    /// retry when the login itself is refused (SQLSTATE 28000).
    /// </summary>
    public async Task<SqlConnection> ConnectionAsync(CancellationToken ct = default)
    {
        if (_connection is not null) return _connection;
        for (var attempt = 1; ; attempt++)
        {
            var cn = factory.Create();
            cn.ConnectionString = new SqlConnectionStringBuilder(cn.ConnectionString) { ConnectTimeout = LoginTimeoutSeconds }.ConnectionString;
            try
            {
                await cn.OpenAsync(ct);
                _connection = cn;
                return cn;
            }
            catch (SqlException ex)
            {
                await cn.DisposeAsync();
                if (LegacyDriverText.SqlState(ex) == "28000") throw;
                await Delay(TimeSpan.FromMilliseconds(2000 * attempt), ct);
                if (attempt >= LoginAttempts) throw;
            }
            catch
            {
                await cn.DisposeAsync();
                throw;
            }
        }
    }

    public async Task BeginAsync(CancellationToken ct = default)
    {
        if (_manualCommit && _transaction?.Connection is not null) return;
        var cn = await ConnectionAsync(ct);
        if (_transaction is not null) await _transaction.DisposeAsync();
        _transaction = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        _manualCommit = true;
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        _manualCommit = false;
        if (_transaction is null) return;
        var t = _transaction;
        _transaction = null;
        try
        {
            // nothing to commit when the server has already ended the unit (SQLEndTran on no active transaction)
            if (t.Connection is not null) await t.CommitAsync(ct);
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        _manualCommit = false;
        if (_transaction is null) return;
        var t = _transaction;
        _transaction = null;
        try
        {
            // a server-side abort (XACT_ABORT) may already have ended the transaction
            if (t.Connection is not null) await t.RollbackAsync(ct);
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    /// <summary>
    /// End of the request/job step (the target equivalent of the legacy process ending): an open unit is rolled back
    /// (accidental carry-over between calls is not reproduced, contracts 6.9).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await RollbackAsync();
        }
        catch
        {
            // the connection may already be broken; nothing to roll back
        }
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
