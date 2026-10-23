using STERP.Testing;

namespace STERP.Target.Parity;

/// <summary>
/// One test case per authored acceptance/edge scenario (tests/cases, runner "api"): each Agent 06
/// acceptance criterion of every application requirement, executed through the binding API/job contracts
/// against the isolated database. Red until Agent 08 delivers the slice.
/// </summary>
public sealed class AcceptanceScenarioTests
{
    public static IEnumerable<ITheoryDataRow> Scenarios() =>
        CaseCatalog.Scenarios.Where(s => s.Runner == "api").Select(s =>
            new TheoryDataRow<string>(s.Id)
                .WithTrait("tier", "target-parity")
                .WithTrait("layer", s.Layer)
                .WithTrait("application", s.ApplicationId)
                .WithTrait("requirement", s.RequirementId));

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Requirement_behaves_as_the_legacy_application(string scenarioId)
    {
        var scenario = CaseCatalog.Get(scenarioId);
        await new ScenarioRunner().RunAsync(scenario);
    }
}
