using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Hosting.PlatformContracts;

namespace Sterp.Modules.Platform;

/// <summary>STERP-PLATFORM (W0): platform and system services slice (STERP-PLATFORM.md).</summary>
public sealed class PlatformModule : ISterpModule
{
    public string Slice => "STERP-PLATFORM";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IGenericMaintenance, Maintenance.GenericMaintenance>();
    }

    public void MapOperations(OperationRegistry operations)
    {
        Session.SessionEndpoints.Map(operations);
        Menu.MenuEndpoints.Map(operations);
        Lookups.LookupEndpoints.Map(operations);
        Worklists.WorklistEndpoints.Map(operations);
        Maintenance.MaintenanceEndpoints.Map(operations);
        Reports.ReportCatalogueEndpoints.Map(operations);
        Console.OperatorConsoleEndpoints.Map(operations);
        Diagnostics.DiagnosticsEndpoints.Map(operations);
        JobEndpoints.RegisterRunResources(operations);
    }
}
