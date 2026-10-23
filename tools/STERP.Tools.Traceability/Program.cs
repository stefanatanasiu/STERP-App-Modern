using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.Composition;
using Sterp.Platform.Hosting.Contracts;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

// Dumps, without any database access, which C# member serves every binding operation and job of the composed modules.
var output = args.Length > 0 ? args[0] : "registry-dump.json";
var registry = new OperationRegistry();
var catalog = new JobCatalog();
var services = new ServiceCollection();
var configuration = new ConfigurationBuilder().Build();
var moduleRows = new JsonArray();
foreach (var m in SterpModules.All())
{
    var opsBefore = registry.Handled.Count;
    var jobsBefore = catalog.All.Count;
    m.ConfigureServices(services, configuration);
    m.MapJobs(catalog);
    m.MapOperations(registry);
    moduleRows.Add(new JsonObject
    {
        ["slice"] = m.Slice,
        ["moduleType"] = m.GetType().FullName,
        ["assembly"] = m.GetType().Assembly.GetName().Name,
        ["operations"] = registry.Handled.Count - opsBefore,
        ["jobs"] = catalog.All.Count - jobsBefore,
    });
}
foreach (var d in catalog.All.Where(d => d.SubmitOperationId is not null && !registry.Handled.Contains(d.SubmitOperationId)))
{
    var definition = d;
    registry.Handle(d.SubmitOperationId!, ctx => JobEndpoints.SubmitFromSliceAsync(ctx, definition));
}

static JsonObject Member(Delegate? d)
{
    if (d is null) return new JsonObject();
    var m = d.Method;
    var t = m.DeclaringType;
    while (t is not null && t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false) && t.DeclaringType is not null)
    {
        t = t.DeclaringType;
    }
    return new JsonObject
    {
        ["type"] = t?.FullName,
        ["method"] = m.Name,
        ["assembly"] = m.DeclaringType?.Assembly.GetName().Name,
        ["lambda"] = m.Name.Contains('<', StringComparison.Ordinal),
    };
}

var operations = new JsonArray();
foreach (var op in ContractCatalog.Instance.Operations.OrderBy(o => o.Document, StringComparer.Ordinal).ThenBy(o => o.OperationId, StringComparer.Ordinal))
{
    var handled = registry.TryGet(op.OperationId, out var handler);
    var isHost = op.Document is "STERP-API" or "STERP-JOBS";
    operations.Add(new JsonObject
    {
        ["operationId"] = op.OperationId,
        ["document"] = op.Document,
        ["method"] = op.Method,
        ["path"] = op.Path,
        ["host"] = op.IsJobsHost ? "STERP.Jobs" : "STERP.Api",
        ["requirements"] = new JsonArray(op.Requirements.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        ["handled"] = handled || isHost,
        ["handler"] = isHost ? new JsonObject { ["type"] = "Sterp.Platform.Hosting.(HealthEndpoints|OpenApiEndpoints)", ["method"] = "host endpoint" } : Member(handled ? handler : null),
    });
}

var jobs = new JsonArray();
foreach (var d in catalog.All.OrderBy(j => j.Slice, StringComparer.Ordinal).ThenBy(j => j.JobCode, StringComparer.Ordinal))
{
    jobs.Add(new JsonObject
    {
        ["jobCode"] = d.JobCode,
        ["slice"] = d.Slice,
        ["program"] = d.Program,
        ["isStream"] = d.IsStream,
        ["submitOperationId"] = d.SubmitOperationId,
        ["execute"] = Member(d.Execute),
        ["preCheck"] = d.PreCheck is null ? null : Member(d.PreCheck),
        ["selfRegistration"] = d.SelfRegistration is not null,
        ["stepPrograms"] = new JsonArray(d.StepPrograms.Select(s => (JsonNode?)new JsonObject { ["step"] = s.Step, ["program"] = s.Program, ["slice"] = s.Slice }).ToArray()),
    });
}

var doc = new JsonObject
{
    ["schema"] = "sterp-registry-dump/1",
    ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
    ["modules"] = moduleRows,
    ["deliveredSlices"] = new JsonArray(registry.DeliveredSlices.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
    ["operations"] = operations,
    ["jobs"] = jobs,
    ["registeredServices"] = new JsonArray(services
        .Where(s => s.ServiceType.Namespace?.StartsWith("Sterp", StringComparison.Ordinal) == true)
        .Select(s => (JsonNode?)new JsonObject
        {
            ["service"] = s.ServiceType.FullName,
            ["implementation"] = s.ImplementationType?.FullName,
            ["lifetime"] = s.Lifetime.ToString(),
        }).ToArray()),
};
File.WriteAllText(output, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{operations.Count} operations ({operations.Count(o => o!["handled"]!.GetValue<bool>())} handled), {jobs.Count} jobs -> {Path.GetFullPath(output)}");
