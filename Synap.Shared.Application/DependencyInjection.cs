using Mapster;
using Microsoft.Extensions.DependencyInjection;
using SergioIzq.Application.Kernel.DependencyInjection;

namespace Synap.Shared.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddSharedApplication(this IServiceCollection services)
    {
        var config = TypeAdapterConfig.GlobalSettings;
        config.Scan(typeof(DependencyInjection).Assembly);

        services.AddMarkedServices(typeof(DependencyInjection).Assembly);

        return services;
    }
}
