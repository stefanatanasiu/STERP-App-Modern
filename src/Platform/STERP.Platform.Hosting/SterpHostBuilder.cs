using System.Text;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sterp.Platform.Hosting.Health;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Hosting;

/// <summary>Composition of STERP.Api ("online") and STERP.Jobs ("batch") (STERP-API.md section 6, STERP-JOBS.md section 6).</summary>
public static class SterpHostBuilder
{
    public const string CorsPolicy = "sterp-gui-and-portal";

    public static WebApplicationBuilder AddSterpHost(this WebApplicationBuilder builder, string engine, params ISterpModule[] modules)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var services = builder.Services;
        var config = builder.Configuration;

        // Negotiate (Windows) authentication is connection based: HTTP/1.1 only (contracts 5.1, 5.8)
        builder.WebHost.ConfigureKestrel(k => k.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http1));

        services.Configure<SterpDatabaseOptions>(config.GetSection(SterpDatabaseOptions.Section));
        services.PostConfigure<SterpDatabaseOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(config[$"{SterpDatabaseOptions.Section}:ApplicationName"]))
            {
                o.ApplicationName = engine == "batch" ? "STERP.Jobs" : "STERP.Api";
            }
        });
        services.Configure<SterpHostOptions>(config.GetSection(SterpHostOptions.Section));
        services.PostConfigure<SterpHostOptions>(o => o.Engine = engine);
        services.Configure<SterpJobOptions>(config.GetSection(SterpJobOptions.Section));
        var contentRoot = builder.Environment.ContentRootPath;
        services.PostConfigure<SterpJobOptions>(o =>
        {
            o.OutputRoot = SterpPaths.Resolve(o.OutputRoot, contentRoot);
            o.LegacyRunRoot = SterpPaths.Resolve(o.LegacyRunRoot, contentRoot);
        });

        services.AddMemoryCache();
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IProcedureRunner, ProcedureRunner>();
        services.AddScoped<SterpContext>();
        services.AddScoped<IUnitOfWork, EngineUnitOfWork>();
        services.AddScoped<ISqlRunner, SqlRunner>();
        services.AddSingleton<UserContextResolver>();
        services.AddSingleton<SchemaDriftCheck>();
        services.AddSingleton(sp => new OutputPaths(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SterpJobOptions>>().Value));
        services.AddSingleton<JobManifestStore>();
        services.AddSingleton<JobService>();
        if (engine == "batch")
        {
            services.AddSingleton<IReadinessCheck, OutputFoldersCheck>();
            services.AddSingleton<IReadinessCheck, JobQueueCheck>();
        }
        Kernel.Services.KernelServices.AddSterpKernel(services);

        var registry = new OperationRegistry();
        var catalog = new JobCatalog();
        // Sterp:Host:UndeliveredSlices withdraws a slice (staged delivery, per-slice rollback; DEPLOYMENT.md section 6): it is
        // not reported as delivered, so readiness, the OpenAPI index, the GUI placeholder and the portal agree with the 501s.
        var withdrawn = config.GetSection($"{SterpHostOptions.Section}:UndeliveredSlices").Get<string[]>() ?? [];
        foreach (var m in modules)
        {
            m.ConfigureServices(services, config);
            var jobsBefore = catalog.All.Count;
            var opsBefore = registry.Handled.Count;
            m.MapJobs(catalog);
            m.MapOperations(registry);
            // a slice is delivered once its module contributes operation handlers or jobs (FR-STERP-API-002)
            if ((catalog.All.Count > jobsBefore || registry.Handled.Count > opsBefore) &&
                !withdrawn.Contains(m.Slice, StringComparer.OrdinalIgnoreCase))
            {
                registry.MarkDelivered(m.Slice);
            }
        }
        foreach (var d in catalog.All.Where(d => d.SubmitOperationId is not null && !registry.Handled.Contains(d.SubmitOperationId)))
        {
            var definition = d;
            registry.Handle(d.SubmitOperationId!, ctx => JobEndpoints.SubmitFromSliceAsync(ctx, definition));
        }
        services.AddSingleton(registry);
        services.AddSingleton(catalog);
        services.AddSingleton(new SterpModuleSet(modules));

        services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate(o =>
        {
            o.Events = new NegotiateEvents
            {
                OnChallenge = async ctx =>
                {
                    ctx.HandleResponse();
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.Headers.Append(HeaderNames.WWWAuthenticate, "Negotiate");
                    ctx.Response.ContentType = "application/problem+json";
                    await ctx.Response.WriteAsync(
                        "{\"type\":\"" + ProblemResults.TypeBase + ProblemKinds.Unauthenticated + "\",\"title\":\"Not authenticated\",\"status\":401," +
                        "\"detail\":\"Windows authentication (Negotiate) is required.\",\"reasonCode\":\"" + ApiReasons.NotAuthenticated + "\"}");
                },
            };
        });
        services.AddAuthorization(o =>
        {
            o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        var origins = config.GetSection($"{SterpHostOptions.Section}:CorsOrigins").Get<string[]>() ?? new SterpHostOptions().CorsOrigins;
        services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
            .WithOrigins(origins)
            .AllowCredentials()
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("Location", "traceparent")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fff ";
        });

        var serviceName = engine == "batch" ? "STERP.Jobs" : "STERP.Api";
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(SterpHostBuilder).Assembly.GetName().Version?.ToString()))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(SterpMetrics.MeterName));
        if (!string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }
        return builder;
    }

    public static WebApplication UseSterpHost(this WebApplication app, string engine)
    {
        // fail fast when the configured server is the shared one while IsolatedOnly is set
        _ = app.Services.GetRequiredService<ISqlConnectionFactory>();
        if (engine == "batch")
        {
            var lost = app.Services.GetRequiredService<JobManifestStore>().MarkLost();
            if (lost > 0) app.Logger.LogWarning("{Count} job run(s) were executing when the job host stopped; their manifests are LOST", lost);
        }
        app.UseHttpsRedirection();
        app.UseRouting();
        app.UseCors(CorsPolicy);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSterpHealth(engine);
        app.MapOpenApiDocuments(engine);
        app.MapContractOperations(engine);
        return app;
    }
}

public static class SterpMetrics
{
    public const string MeterName = "STERP";
}

/// <summary>Configured folder paths: absolute values are kept as written, relative ones are resolved against a base folder.</summary>
public static class SterpPaths
{
    public static string Resolve(string path, string baseDirectory) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path, baseDirectory);
}
