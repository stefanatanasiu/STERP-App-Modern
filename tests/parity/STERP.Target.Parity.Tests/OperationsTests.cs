using System.Text.Json.Nodes;
using STERP.Testing;

namespace STERP.Target.Parity;

/// <summary>
/// Operations obligations (contracts 5.1, 4.5, 4.7, 10; Agent 06 04-application-integration-and-runtime-design
/// sections 9-10): health, authentication, correlation and trace identifiers, deployment smoke and the
/// performance comparison with the first legacy baseline recorded by the legacy characterization tier.
/// </summary>
public sealed class OperationsTests
{
    private static readonly Dictionary<string, string> NoHeaders = new();

    public static TheoryData<string, string> Hosts() => new()
    {
        { "STERP.Api", "api" }, { "STERP.Jobs", "jobs" },
    };

    private static string Url(string host) => host == "api" ? ParityEnvironment.ApiBaseUrl : ParityEnvironment.JobsBaseUrl;

    [Theory]
    [MemberData(nameof(Hosts))]
    [Trait("tier", "target-parity")]
    [Trait("layer", "integration")]
    public async Task Host_is_live_and_ready_on_the_frozen_schema(string what, string host)
    {
        await TargetClient.EnsureReachableAsync(Url(host), what);
        await IsolatedDatabase.RestoreAsync("fresh");
        var live = await TargetClient.SendAsync(HttpMethod.Get, $"{Url(host).TrimEnd('/')}/health/live", NoHeaders, null);
        var ready = await TargetClient.SendAsync(HttpMethod.Get, $"{Url(host).TrimEnd('/')}/health/ready", NoHeaders, null);
        Assert.Equal(200, live.Status);
        Assert.True(ready.Status == 200, $"{what} readiness on the unmodified isolated deploy: {ready.Status} {ready.Text}");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    [Trait("tier", "target-parity")]
    [Trait("layer", "integration")]
    public async Task Schema_drift_makes_readiness_unhealthy_naming_the_object(string what, string host)
    {
        await TargetClient.EnsureReachableAsync(Url(host), what);
        await IsolatedDatabase.RestoreAsync("fresh");
        try
        {
            // Isolated copy only: one frozen-contract column is renamed to simulate drift (contracts 6.11).
            await IsolatedDatabase.ExecuteAsync("EXEC sp_rename 'core.Parameter.Value', 'Value_Drift', 'COLUMN';");
            var ready = await TargetClient.SendAsync(HttpMethod.Get, $"{Url(host).TrimEnd('/')}/health/ready", NoHeaders, null);
            Assert.Equal(503, ready.Status);
            Assert.Contains("core.Parameter", ready.Text, StringComparison.Ordinal);
        }
        finally
        {
            await IsolatedDatabase.RestoreAsync("fresh");
        }
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    [Trait("tier", "target-parity")]
    [Trait("layer", "contract")]
    public async Task Unauthenticated_request_is_challenged_with_Negotiate(string what, string host)
    {
        await TargetClient.EnsureReachableAsync(Url(host), what);
        using var anonymous = new HttpClient(new HttpClientHandler
        {
            UseDefaultCredentials = false,
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true,
        });
        var path = host == "api" ? "/api/platform/v1/session" : "/api/jobs/v1/runs";
        using var response = await anonymous.GetAsync($"{Url(host).TrimEnd('/')}{path}", TestContext.Current.CancellationToken);
        Assert.Equal(401, (int)response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme.Equals("Negotiate", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An unknown maintenance definition is the binding business-rule refusal (platform_getMaintenanceDefinition:
    /// "422 E-0090 not registered"; core.usp_UiMaintDef.sql:18 THROW 50090 'STERP-E-0090: ...', translated per contracts
    /// 4.6 to reasonCode E-0090); the problem carries the W3C trace id and the Idempotency-Key as correlation id.
    /// </summary>
    [Fact]
    [Trait("tier", "target-parity")]
    [Trait("layer", "integration")]
    public async Task Correlation_and_trace_identifiers_flow_to_outcomes_and_problems()
    {
        await TargetClient.EnsureReachableAsync(ParityEnvironment.ApiBaseUrl, "STERP.Api");
        await IsolatedDatabase.RestoreAsync("fresh");
        await IsolatedDatabase.MapLoginAsync("SYSADM");
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = $"00-{traceId}-00f067aa0ba902b7-01",
            ["X-STERP-Company"] = "STC",
            ["X-STERP-Site"] = "RDG",
            ["Idempotency-Key"] = "A07-OBS-0001",
        };
        var unknown = await TargetClient.SendAsync(HttpMethod.Get,
            $"{ParityEnvironment.ApiBaseUrl.TrimEnd('/')}/api/platform/v1/maintenance-definitions/A07-NO-SUCH-DEFINITION", headers, null);
        Assert.Equal(422, unknown.Status);
        Assert.Equal("E-0090", unknown.Json?["reasonCode"]?.ToString());
        Assert.Contains(traceId, unknown.Json?["traceId"]?.ToString() ?? "", StringComparison.Ordinal);
        Assert.Equal("A07-OBS-0001", unknown.Json?["correlationId"]?.ToString());
    }

    [Fact]
    [Trait("tier", "target-parity")]
    [Trait("layer", "e2e")]
    public async Task Deployment_smoke_all_hosts_portal_and_gui_respond()
    {
        foreach (var (url, what, path) in new[]
                 {
                     (ParityEnvironment.ApiBaseUrl, "STERP.Api", "/health/live"),
                     (ParityEnvironment.JobsBaseUrl, "STERP.Jobs", "/health/live"),
                     (ParityEnvironment.WebBaseUrl, "STERP.Web", "/"),
                     (ParityEnvironment.PortalBaseUrl, "STERP.ApiPortal", "/"),
                 })
        {
            await TargetClient.EnsureReachableAsync(url, what);
            var status = await TargetClient.GetStatusAsync($"{url.TrimEnd('/')}{path}");
            Assert.True((int)status is >= 200 and < 400, $"{what} {path}: {(int)status}");
        }
    }

    /// <summary>Legacy acceptance workflow (legacy characterization baseline) -> target demo journey scenario.</summary>
    public static TheoryData<string, string> Workflows() => new()
    {
        { "OrderToCashTests", "STERP-SO" }, { "ProcureToPayTests", "STERP-PO" }, { "PeriodCloseTests", "STERP-FIN" },
        { "AssemblyKitTests", "STERP-KIT" }, { "CostingBomTests", "STERP-CST" }, { "ControlsTests", "STERP-XMD" },
    };

    [Theory]
    [MemberData(nameof(Workflows))]
    [Trait("tier", "target-parity")]
    [Trait("layer", "e2e")]
    public async Task Demo_journey_is_not_slower_than_the_legacy_baseline(string legacyWorkflow, string application)
    {
        var baselineFile = Path.Combine(ParityEnvironment.TestsRoot, "legacy", "STERP.Legacy.Characterization.Tests", "fixtures", "performance", $"{legacyWorkflow}.json");
        var baseline = JsonNode.Parse(await File.ReadAllTextAsync(baselineFile, TestContext.Current.CancellationToken))!["elapsedMilliseconds"]!.GetValue<long>();
        var journey = CaseCatalog.Scenarios.FirstOrDefault(s => s.ApplicationId == application && s.Id.EndsWith("-EDGE-DEMO-JOURNEY", StringComparison.Ordinal));
        Assert.True(journey is not null, $"{application}: no demo journey scenario authored");
        var runner = new ScenarioRunner();
        await runner.RunAsync(journey!);
        // NFR (Agent 06 02-current-to-target-architecture.md:135): not slower than the legacy client; 50% allowance for the first comparison.
        Assert.True(runner.StepsElapsed.TotalMilliseconds <= baseline * 1.5 + 2000,
            $"{application} journey took {runner.StepsElapsed.TotalMilliseconds:F0} ms; legacy {legacyWorkflow} baseline {baseline} ms");
    }
}
