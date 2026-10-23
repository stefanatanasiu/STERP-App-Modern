using STERP.Testing;

namespace STERP.Target.Gui.Parity;

/// <summary>
/// One test case per authored GUI scenario (tests/cases, runner "gui"): page, menu, key, context-change,
/// formatting and accessibility behaviour of STERP-WEB and the slice pages. Red until Agent 08 delivers them.
/// </summary>
public sealed class GuiScenarioTests
{
    public static IEnumerable<ITheoryDataRow> Scenarios() =>
        CaseCatalog.Scenarios.Where(s => s.Runner == "gui").Select(s =>
            new TheoryDataRow<string>(s.Id)
                .WithTrait("tier", "target-parity")
                .WithTrait("layer", s.Layer)
                .WithTrait("application", s.ApplicationId)
                .WithTrait("requirement", s.RequirementId));

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Page_behaves_as_the_legacy_screen(string scenarioId)
    {
        await new ScenarioRunner(new PlaywrightGuiDriver()).RunAsync(CaseCatalog.Get(scenarioId));
    }

    public static IEnumerable<ITheoryDataRow> Routes() =>
        BindingContracts.GuiRoutes
            .Select(r => new TheoryDataRow<string>(r).WithTrait("tier", "target-parity").WithTrait("layer", "ui-journey"));

    /// <summary>STERP-WEB section 10 smoke sweep: every routed page (or its placeholder) opens for SYSADM without an error cue.</summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Routed_page_opens_without_error_for_SYSADM(string route)
    {
        await TargetClient.EnsureReachableAsync(ParityEnvironment.WebBaseUrl, "STERP.Web");
        await IsolatedDatabase.RestoreAsync("demo");
        await IsolatedDatabase.MapLoginAsync("SYSADM");
        var concrete = route.Replace("{maintCode}", "CORE.COMPANY", StringComparison.Ordinal).Replace("{reportArgument}", "TBRPT", StringComparison.Ordinal);
        var page = await new PlaywrightGuiDriver().RunAsync(
            new ParityScenario($"SMOKE {route}", "FR-STERP-WEB-SMOKE", "STERP-WEB", null, route, "ui-journey", "gui", "demo", "inferred",
                [], [], [], [], "", ""),
            new System.Text.Json.Nodes.JsonObject { ["gui"] = concrete },
            []);
        var text = JsonPathLite.AsText(page["text"]) ?? "";
        Assert.DoesNotContain("*** ERROR", text, StringComparison.Ordinal);
        Assert.DoesNotContain("STERP-E-0004", text, StringComparison.Ordinal);
    }
}
