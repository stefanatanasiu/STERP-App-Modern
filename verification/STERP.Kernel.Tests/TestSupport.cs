using System.Data;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Kernel.Tests;

/// <summary>
/// The isolated database of the kernel work stream. The server comes from STERP_KERNEL_TEST_SERVER (default
/// (localdb)\STERPA08_kernel); anything that is not a LocalDB STERPA08_* instance is refused, so these tests can never
/// reach the shared server. Tests restore what they write: they run inside a unit of work that is rolled back, and the
/// few autocommit tests delete the rows they committed.
/// </summary>
public sealed class LocalDbFixture
{
    public const string ServerVariable = "STERP_KERNEL_TEST_SERVER";
    public const string DefaultServer = @"(localdb)\STERPA08_kernel";

    public LocalDbFixture()
    {
        Server = Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } s ? s.Trim() : DefaultServer;
        Guard(Server);
        ConnectionString = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = "STERP",
            IntegratedSecurity = true,
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
            ApplicationName = "STERP.Kernel.Tests",
            ConnectTimeout = 60,
        }.ConnectionString;
    }

    public string Server { get; }

    public string ConnectionString { get; }

    /// <summary>Refuses every server that is not a LocalDB instance named STERPA08_*.</summary>
    public static void Guard(string server)
    {
        if (!Regex.IsMatch(server, @"^\(localdb\)\\STERPA08_[A-Za-z0-9_]+$", RegexOptions.IgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing database server '{server}': kernel integration tests run only against an isolated LocalDB instance (localdb)\\STERPA08_*.");
        }
    }

    /// <summary>A kernel service provider on the isolated database (the host's data access registrations plus AddSterpKernel).</summary>
    public ServiceProvider Services(string? driverPrefix = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Sterp:Database:ConnectionString"] = ConnectionString,
            ["Sterp:Database:IsolatedOnly"] = "true",
            ["Sterp:Database:ApplicationName"] = "STERP.Kernel.Tests",
        };
        if (driverPrefix is not null) settings["Sterp:Kernel:LegacyDriverMessagePrefix"] = driverPrefix;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<SterpDatabaseOptions>(config.GetSection(SterpDatabaseOptions.Section));
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IProcedureRunner, ProcedureRunner>();
        services.AddScoped<SterpContext>();
        services.AddScoped<IUnitOfWork, EngineUnitOfWork>();
        services.AddScoped<ISqlRunner, SqlRunner>();
        services.AddSterpKernel();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>A scalar read on a separate, autocommit connection (sees only committed data).</summary>
    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
    {
        await using var cn = new SqlConnection(ConnectionString);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand(sql, cn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? default : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A statement on a separate autocommit connection (cleanup of committed test data only).</summary>
    public async Task<int> ExecuteAsync(string sql, params (string Name, object? Value)[] args)
    {
        await using var cn = new SqlConnection(ConnectionString);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand(sql, cn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class LocalDbCollection : ICollectionFixture<LocalDbFixture>
{
    public const string Name = "LocalDb";
}

/// <summary>An engine connection factory for one connection string (LocalDB STERPA08_* only) that counts its connections.</summary>
public sealed class CountingConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public CountingConnectionFactory(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        LocalDbFixture.Guard(b.DataSource);
        _connectionString = connectionString;
        DataSource = b.DataSource;
        Database = b.InitialCatalog;
    }

    public int Created { get; private set; }

    public string DataSource { get; }

    public string Database { get; }

    public SqlConnection Create()
    {
        Created++;
        return new SqlConnection(_connectionString);
    }
}

/// <summary>An ISqlRunner that fails every statement: proves that a refusal is decided without any database call.</summary>
public sealed class NoSqlRunner : ISqlRunner
{
    public IUnitOfWork UnitOfWork => throw Fail();

    private static InvalidOperationException Fail() => new("No SQL statement may be executed on this path.");

    public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default) => throw Fail();
    public Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default) => throw Fail();
    public Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default) => throw Fail();
    public Task<T?> ScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default) => throw Fail();
    public Task<JsonArray> QueryJsonAsync(string sql, object? param = null, CancellationToken ct = default) => throw Fail();
    public Task<T> WithReaderAsync<T>(string sql, object? param, Func<System.Data.Common.DbDataReader, Task<T>> read, CancellationToken ct = default) => throw Fail();
    public Task<int> ExecuteCommandAsync(string sql, CommandType type, Action<SqlParameterCollection> parameters, CancellationToken ct = default) => throw Fail();
    public string DriverText(SqlException ex) => ex.Message;
}

/// <summary>An ISqlRunner that raises a given SQLIO failure for statements matching a pattern and delegates the rest.</summary>
public sealed class FailingSqlRunner(ISqlRunner inner, Func<string, bool> fails, SqlioException failure) : ISqlRunner
{
    public int Failures { get; private set; }

    public IUnitOfWork UnitOfWork => inner.UnitOfWork;

    private void Check(string sql)
    {
        if (!fails(sql)) return;
        Failures++;
        throw failure;
    }

    public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default) { Check(sql); return inner.QueryAsync<T>(sql, param, ct); }
    public Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default) { Check(sql); return inner.QueryFirstOrDefaultAsync<T>(sql, param, ct); }
    public Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default) { Check(sql); return inner.ExecuteAsync(sql, param, ct); }
    public Task<T?> ScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default) { Check(sql); return inner.ScalarAsync<T>(sql, param, ct); }
    public Task<JsonArray> QueryJsonAsync(string sql, object? param = null, CancellationToken ct = default) { Check(sql); return inner.QueryJsonAsync(sql, param, ct); }
    public Task<T> WithReaderAsync<T>(string sql, object? param, Func<System.Data.Common.DbDataReader, Task<T>> read, CancellationToken ct = default) { Check(sql); return inner.WithReaderAsync(sql, param, read, ct); }
    public Task<int> ExecuteCommandAsync(string sql, CommandType type, Action<SqlParameterCollection> parameters, CancellationToken ct = default) { Check(sql); return inner.ExecuteCommandAsync(sql, type, parameters, ct); }
    public string DriverText(SqlException ex) => inner.DriverText(ex);
}

/// <summary>Records every statement and lets a test rewrite selected ones (for example to hide a catalog view).</summary>
public sealed class RewritingSqlRunner(ISqlRunner inner, Func<string, string> rewrite) : ISqlRunner
{
    public List<string> Statements { get; } = [];

    public IUnitOfWork UnitOfWork => inner.UnitOfWork;

    private string R(string sql)
    {
        Statements.Add(sql);
        return rewrite(sql);
    }

    public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default) => inner.QueryAsync<T>(R(sql), param, ct);
    public Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default) => inner.QueryFirstOrDefaultAsync<T>(R(sql), param, ct);
    public Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default) => inner.ExecuteAsync(R(sql), param, ct);
    public Task<T?> ScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default) => inner.ScalarAsync<T>(R(sql), param, ct);
    public Task<JsonArray> QueryJsonAsync(string sql, object? param = null, CancellationToken ct = default) => inner.QueryJsonAsync(R(sql), param, ct);
    public Task<T> WithReaderAsync<T>(string sql, object? param, Func<System.Data.Common.DbDataReader, Task<T>> read, CancellationToken ct = default) => inner.WithReaderAsync(R(sql), param, read, ct);
    public Task<int> ExecuteCommandAsync(string sql, CommandType type, Action<SqlParameterCollection> parameters, CancellationToken ct = default) => inner.ExecuteCommandAsync(R(sql), type, parameters, ct);
    public string DriverText(SqlException ex) => inner.DriverText(ex);
}

public static class Callers
{
    /// <summary>A header as a legacy caller filled it.</summary>
    public static KernelCaller For(string company, string user, int businessDate = 20261019, string site = "", string correlation = "") =>
        new(company, site, user, businessDate, correlation);
}
