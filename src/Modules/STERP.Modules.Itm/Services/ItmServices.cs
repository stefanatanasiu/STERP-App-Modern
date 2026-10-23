using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Data;
using Sterp.Platform.Kernel;

namespace Sterp.Modules.Itm.Services;

/// <summary>IC-ITM-STKMOVE over <see cref="StkMoveProgram"/>.</summary>
internal sealed class StockMovementService(IServiceProvider services) : IStockMovementService
{
    public async Task<LegacyResult> ExecuteAsync(string function, StkmoveCommarea area, ItmCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(call);
        var ca = CaHeader.For(function, call);
        await services.GetRequiredService<StkMoveProgram>().RunAsync(ca, area, ct);
        return ca.Result;
    }
}

/// <summary>IC-ITM-UOMCONV over <see cref="UomConvProgram"/>.</summary>
internal sealed class UnitConversionService(IServiceProvider services) : IUnitConversionService
{
    public async Task<LegacyResult> ExecuteAsync(string function, UomconvCommarea area, ItmCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(call);
        var ca = CaHeader.For(function, call);
        await services.GetRequiredService<UomConvProgram>().RunAsync(ca, area, ct);
        return ca.Result;
    }
}

/// <summary>IC-ITM-LOTSER over <see cref="LotSerProgram"/>.</summary>
internal sealed class LotSerialService(IServiceProvider services) : ILotSerialService
{
    public async Task<LegacyResult> ExecuteAsync(string function, LotserCommarea area, ItmCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(call);
        var ca = CaHeader.For(function, call);
        await services.GetRequiredService<LotSerProgram>().RunAsync(ca, area, ct);
        return ca.Result;
    }
}

/// <summary>IC-ITM-AVAILQ over <see cref="AvailqProgram"/>.</summary>
internal sealed class AvailabilityService(IServiceProvider services) : IAvailabilityService
{
    public async Task<LegacyResult> ExecuteAsync(string function, AvailqCommarea area, ItmCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(call);
        var ca = CaHeader.For(function, call);
        await services.GetRequiredService<AvailqProgram>().RunAsync(ca, area, ct);
        return ca.Result;
    }
}

/// <summary>IC-ITM-STKRESV over <see cref="StkResvProgram"/>.</summary>
internal sealed class StockReservationService(IServiceProvider services) : IStockReservationService
{
    public async Task<LegacyResult> ExecuteAsync(string function, StkresvCommarea area, ItmCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(call);
        var ca = CaHeader.For(function, call);
        await services.GetRequiredService<StkResvProgram>().RunAsync(ca, area, ct);
        return ca.Result;
    }
}

/// <summary>IC-ITM-SUBSTCHK over <see cref="SubstChkProgram"/>.</summary>
internal sealed class SubstitutionCheckService(IServiceProvider services) : ISubstitutionCheckService
{
    public async Task<LegacyResult> ExecuteAsync(string function, SubstchkCommarea area, ItmCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(call);
        var ca = CaHeader.For(function, call);
        await services.GetRequiredService<SubstChkProgram>().RunAsync(ca, area, ct);
        return ca.Result;
    }
}

/// <summary>DI registration of the ITM programs and public contracts (scoped data access, one program instance per call).</summary>
internal static class ItmServiceRegistration
{
    public static IServiceCollection AddItmPrograms(this IServiceCollection services)
    {
        services.AddScoped<Sqlio>();
        services.AddScoped<IItmCalendar, KernelItmCalendar>();
        services.AddTransient<ItmDateProgram>();
        services.AddTransient<ItemValProgram>();
        services.AddTransient<UomConvProgram>();
        services.AddTransient<LotSerProgram>();
        services.AddTransient<StkMoveProgram>();
        services.AddTransient<AvailqProgram>();
        services.AddTransient<ItmComplProgram>();
        services.AddTransient<StkResvProgram>();
        services.AddTransient<SubstChkProgram>();
        services.AddScoped<IStockMovementService, StockMovementService>();
        services.AddScoped<IUnitConversionService, UnitConversionService>();
        services.AddScoped<ILotSerialService, LotSerialService>();
        services.AddScoped<IAvailabilityService, AvailabilityService>();
        services.AddScoped<IStockReservationService, StockReservationService>();
        services.AddScoped<ISubstitutionCheckService, SubstitutionCheckService>();
        return services;
    }
}
