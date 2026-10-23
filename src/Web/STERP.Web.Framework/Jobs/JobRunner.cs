using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Web.Framework.Api;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Framework.Jobs;

/// <summary>Final state of a submitted run (core.JobRun row plus the target execution block).</summary>
public sealed record JobRunOutcome(ApiResponse Submit, JsonNode? Run, long JobRunId)
{
    public bool Accepted => Submit.Status == 202;

    public string Status => Json.Str(Run?["status"]);

    public int? ReturnCode => Run?["returnCode"] is JsonValue v && v.TryGetValue<int>(out var rc) ? rc
        : Run?["execution"]?["exitCode"] is JsonValue e && e.TryGetValue<int>(out var ec) ? ec : null;

    public string Message => Json.Str(Run?["message"]);

    /// <summary>The spool file the run reported (core.JobRun.SpoolFile, else the first spool of the execution).</summary>
    public string SpoolFile => Json.Str(Run?["spoolFile"]) is { Length: > 0 } s ? s
        : (Run?["execution"]?["spoolFiles"] as JsonArray)?.Select(Json.Str).FirstOrDefault(x => x.Length > 0) ?? "";

    public string LogFile => Json.Str(Run?["logFile"]);
}

/// <summary>
/// Job submission from a page (FR-STERP-WEB-018): submits (202 JobRunAccepted), polls GET /api/jobs/v1/runs/{id} until the
/// run has ended and returns it, so the page can open the spool viewer as frmSalesInvoiceRun opened frmSpoolViewer.
/// </summary>
public sealed class JobRunner(ApiClient api, BrowserInterop browser)
{
    private static readonly string[] Terminal = ["OK", "WARNING", "FAILED", "ABENDED", "CANCELLED", "COMPLETE", "LOST"];

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(400);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);

    public async Task<JobRunOutcome> RunAsync(Func<Task<ApiResponse>> submit, CancellationToken ct = default)
    {
        using var busy = browser.Busy();
        var accepted = await submit();
        if (accepted.Status != 202) return new JobRunOutcome(accepted, null, 0);
        var id = Json.Long(accepted.Json?["jobRunId"]);
        if (id == 0 && accepted.Header("location") is { } location &&
            long.TryParse(location.TrimEnd('/').Split('/').Last(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromLocation))
        {
            id = fromLocation;
        }
        if (id == 0) return new JobRunOutcome(accepted, accepted.Json, 0);
        var deadline = DateTime.UtcNow + Timeout;
        JsonNode? run = null;
        while (DateTime.UtcNow < deadline)
        {
            var r = await api.GetAsync(ApiHost.Jobs, $"/api/jobs/v1/runs/{id}", ct: ct);
            if (!r.IsSuccess)
            {
                run ??= accepted.Json;
                break;
            }
            run = r.Json;
            var state = Json.Str(run?["execution"]?["state"]);
            var status = Json.Str(run?["status"]);
            if (state is "ENDED" or "LOST" || (state.Length == 0 && Terminal.Contains(status))) break;
            await Task.Delay(PollInterval, ct);
        }
        return new JobRunOutcome(accepted, run, id);
    }
}
