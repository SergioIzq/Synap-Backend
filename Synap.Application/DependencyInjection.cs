using MediatR;
using Microsoft.Extensions.DependencyInjection;
using SergioIzq.Application.Kernel.DependencyInjection;

namespace Synap.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));

        services.AddMarkedServices(typeof(DependencyInjection).Assembly);
        services.AddKernelDependencyOrchestration();

        return services;
    }
}
