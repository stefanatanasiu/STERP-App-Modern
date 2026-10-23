using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>The PING HEALTH answer (PINGCA.cpy:4-16): STCAHDR outcome plus database name, server time and version.</summary>
public sealed record DatabaseProbeResult(LegacyResult Result, string DatabaseName, string ServerTime, string Version)
{
    public bool Available => Result.ReturnCode == 0;
}

/// <summary>PING - database health probe (FR-STERP-PLATFORM-030), the data source of /health/ready and the service status.</summary>
public interface IDatabaseProbe
{
    /// <summary>
    /// One single-row query: DB_NAME() (30), CONVERT(varchar(30), SYSDATETIME(), 121) (30), LEFT(@@VERSION, 80). Success is
    /// RC 00 'ENGINE AND DATABASE AVAILABLE'; a failure is a warning RC 04 SQL0001 with the first 200 bytes of the driver
    /// text (BR-PING-002), never an error.
    /// </summary>
    Task<DatabaseProbeResult> ProbeAsync(CancellationToken ct = default);
}

/// <summary>
/// PING.cob:23-55. The legacy probe ran on the engine connection (SQLIO slot 1); the target probe opens its own short
/// connection so that it can serve the readiness endpoint outside any request unit of work (read-only, no data effect).
/// The engine version literal and the ECHO numeric round trip belong to the COBOL engine and are not reproduced.
/// </summary>
public sealed class DatabaseProbe(ISqlConnectionFactory factory, IOptions<SterpDatabaseOptions> options) : IDatabaseProbe
{
    public const string Statement = "SELECT DB_NAME(), CONVERT(varchar(30), SYSDATETIME(), 121), LEFT(@@VERSION, 80)";

    public async Task<DatabaseProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var cn = factory.Create();
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(Statement, cn) { CommandTimeout = options.Value.CommandTimeoutSeconds };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                return new DatabaseProbeResult(new LegacyResult(4, "SQL0001", ""), "", "", "");
            }
            string Slot(int i, int w) => r.IsDBNull(i) ? "" : CobolText.Trimmed(r.GetString(i), w);
            return new DatabaseProbeResult(new LegacyResult(0, "", "ENGINE AND DATABASE AVAILABLE"), Slot(0, 30), Slot(1, 30), Slot(2, 80));
        }
        catch (SqlException ex)
        {
            var text = LegacyDriverText.From(ex, options.Value.LegacyDriverMessagePrefix);
            return new DatabaseProbeResult(new LegacyResult(4, "SQL0001", CobolText.Trimmed(text, 200)), "", "", "");
        }
    }
}
