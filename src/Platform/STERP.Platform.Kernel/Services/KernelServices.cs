using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>DI registration of the shared kernel (contracts section 7; STERP-PLATFORM.md section 6.3).</summary>
public static class KernelServices
{
    /// <summary>
    /// Registers the kernel services. Services that run on the request's engine connection (ISqlRunner / IUnitOfWork /
    /// SterpContext) are scoped; pure helpers, the calendar cache and the database probe are singletons. The host registers
    /// ISqlRunner, IUnitOfWork, ISqlConnectionFactory and SterpContext (SterpHostBuilder).
    /// </summary>
    public static IServiceCollection AddSterpKernel(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<SterpKernelOptions>().Configure<IServiceProvider>((o, sp) =>
        {
            var section = sp.GetService<IConfiguration>()?.GetSection(SterpKernelOptions.Section);
            if (section is null) return;
            if (section["CalendarCacheTtl"] is { Length: > 0 } ttl &&
                TimeSpan.TryParse(ttl, System.Globalization.CultureInfo.InvariantCulture, out var t))
            {
                o.CalendarCacheTtl = t;
            }
            var prefix = section.GetSection("LegacyDriverMessagePrefix");
            if (prefix.Value is not null) o.LegacyDriverMessagePrefix = prefix.Value;
        });
        // Sterp:Kernel:LegacyDriverMessagePrefix (the FR-STERP-PLATFORM-010 name) overrides Sterp:Database:LegacyDriverMessagePrefix
        services.AddOptions<SterpDatabaseOptions>().PostConfigure<IOptions<SterpKernelOptions>>((db, kernel) =>
        {
            if (kernel.Value.LegacyDriverMessagePrefix is { } prefix) db.LegacyDriverMessagePrefix = prefix;
        });

        // singletons: pure computation, the calendar cache and the database probe (own short connection)
        services.TryAddSingleton<ICalendarCache, CalendarCache>();
        services.TryAddSingleton<IRoundingAllocator, RoundingAllocator>();
        services.TryAddSingleton<IDatabaseProbe, DatabaseProbe>();

        // scoped: everything that runs on the caller's engine connection and unit of work (SQLIO family)
        services.TryAddScoped<IPermissionService, PermissionService>();
        services.TryAddScoped<IPeriodService, PeriodService>();
        services.TryAddScoped<IDocumentNumberService, DocumentNumberService>();
        services.TryAddScoped<IIdempotencyService, IdempotencyService>();
        services.TryAddScoped<IAuditWriter, AuditWriter>();
        services.TryAddScoped<IExceptionRaiser, ExceptionRaiser>();
        services.TryAddScoped<IParameterService, ParameterService>();
        services.TryAddScoped<IBusinessCalendar, BusinessCalendar>();
        services.TryAddScoped<IPaymentTermsCalculator, PaymentTermsCalculator>();
        services.TryAddScoped<IFxConverter, FxConverter>();
        services.TryAddScoped<IJobRunRegistry, JobRunRegistry>();
        services.TryAddScoped<IGlPostingGateway, GlPostingGateway>();
        return services;
    }
}
