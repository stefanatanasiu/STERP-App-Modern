using System.Data.Common;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Platform.Diagnostics;

/// <summary>PING HEALTH result (PINGCA.cpy:4-16): database name, server time, version and the STCAHDR outcome.</summary>
public sealed record ProbeResult(string DbName, string DbServerTime, string DbVersion, int ReturnCode, string ReasonCode, string Message)
{
    public bool IsOk => ReturnCode == 0;

    public System.Text.Json.Nodes.JsonObject ToJson() => new()
    {
        ["dbName"] = DbName,
        ["dbServerTime"] = DbServerTime,
        ["dbVersion"] = DbVersion,
        ["returnCode"] = ReturnCode,
        ["reasonCode"] = ReasonCode,
        ["message"] = Message,
    };

    /// <summary>EngineService.Status (EngineService.vb:32-42): ONLINE, DEGRADED (answered with a warning) or DOWN.</summary>
    public string EngineStatus => ReturnCode == 0 && DbName.Length > 0 ? "ONLINE" : ReturnCode <= 4 ? "DEGRADED" : "DOWN";
}

/// <summary>
/// The database probe of PING HEALTH (PING.cob:23-55; FR-STERP-PLATFORM-030): one SQLIO single-row SELECT of DB_NAME(),
/// CONVERT(varchar(30), SYSDATETIME(), 121) and LEFT(@@VERSION, 80) into X(30)/X(30)/X(80) fields; OK is 'ENGINE AND
/// DATABASE AVAILABLE', a failure is RC 04 SQL0001 with the failure text (CA-MESSAGE X(200)), never an error.
/// </summary>
public static class DatabaseProbe
{
    public const string ProbeSql = "SELECT DB_NAME(), CONVERT(varchar(30), SYSDATETIME(), 121), LEFT(@@VERSION, 80)";

    public static async Task<ProbeResult> RunAsync(ISqlRunner sql, IOptions<SterpDatabaseOptions> options, CancellationToken ct)
    {
        try
        {
            return await sql.WithReaderAsync(ProbeSql, null, async r =>
            {
                if (!await r.ReadAsync(ct)) return new ProbeResult("", "", "", 0, "", "ENGINE AND DATABASE AVAILABLE");
                return new ProbeResult(Field(r, 0, 30), Field(r, 1, 30), Field(r, 2, 80), 0, "", "ENGINE AND DATABASE AVAILABLE");
            }, ct);
        }
        catch (SqlioException ex)
        {
            return Failed(ex.DriverText);
        }
        catch (SqlException ex)
        {
            return Failed(LegacyDriverText.From(ex, options.Value.LegacyDriverMessagePrefix));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ex.Message);
        }
    }

    private static ProbeResult Failed(string text) => new("", "", "", 4, "SQL0001", CobolText.Trimmed(text, 200));

    private static string Field(DbDataReader r, int i, int width) => r.IsDBNull(i) ? "" : CobolText.Trimmed(Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture), width);

    /// <summary>Informational version of the running host (replaces the COBOL engine version literal).</summary>
    public static string ServiceVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(DatabaseProbe).Assembly;
        var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "";
        return v.Length > 60 ? v[..60] : v;
    }
}
