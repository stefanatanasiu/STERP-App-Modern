using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Hosting.Contracts;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Platform.Hosting;

/// <summary>
/// OpenAPI publication (FR-STERP-API-014, BE-API-04 / BE-JOBS-04): the binding documents of the delivered slices plus the
/// host's own document, served anonymously as the exact binding bytes (.yaml) or their JSON form (.json), with an index.
/// </summary>
public static class OpenApiEndpoints
{
    public static IReadOnlyList<ContractDocument> ServedDocuments(IServiceProvider services, string engine)
    {
        var registry = services.GetRequiredService<OperationRegistry>();
        var hostDoc = engine == "batch" ? "STERP-JOBS" : "STERP-API";
        return ContractCatalog.Instance.Documents
            .Where(d => d.Name == hostDoc || registry.IsDelivered(d.Slice))
            .ToList();
    }

    public static void MapOpenApiDocuments(this IEndpointRouteBuilder app, string engine)
    {
        app.MapGet("/openapi/index.json", (HttpContext http) =>
        {
            var docs = new JsonArray();
            foreach (var d in ServedDocuments(http.RequestServices, engine))
            {
                docs.Add(new JsonObject
                {
                    ["name"] = d.Name,
                    ["title"] = d.Title,
                    ["slice"] = d.Slice,
                    ["wave"] = d.Wave,
                    ["yaml"] = $"/openapi/{d.Name}.yaml",
                    ["json"] = $"/openapi/{d.Name}.json",
                });
            }
            return Results.Json(new JsonObject { ["documents"] = docs }, SterpJson.Options);
        }).AllowAnonymous().WithName(engine == "batch" ? "jobs_listOpenApiDocuments" : "api_listOpenApiDocuments");

        app.MapGet("/openapi/{documentName}.{format}", (HttpContext http, string documentName, string format) =>
        {
            var doc = ServedDocuments(http.RequestServices, engine)
                .FirstOrDefault(d => string.Equals(d.Name, documentName, StringComparison.OrdinalIgnoreCase));
            if (doc is null)
            {
                return ProblemResults.Create(404, Sterp.Platform.Kernel.ProblemKinds.NotFound,
                    $"OpenAPI document {documentName} is not served by this host.", null, "API0404");
            }
            return format.ToLowerInvariant() switch
            {
                "yaml" or "yml" => Results.Bytes(doc.YamlBytes, "application/yaml; charset=utf-8"),
                "json" => Results.Text(doc.Json.ToJsonString(SterpJson.Options), "application/json; charset=utf-8"),
                _ => ProblemResults.Create(404, Sterp.Platform.Kernel.ProblemKinds.NotFound, $"Format {format} is not available (yaml or json).", null, "API0404"),
            };
        }).AllowAnonymous().WithName(engine == "batch" ? "jobs_getOpenApiDocument" : "api_getOpenApiDocument");
    }
}
