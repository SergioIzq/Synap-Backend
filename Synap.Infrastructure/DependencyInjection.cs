using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SergioIzq.AspNetCore.Kernel.DependencyInjection;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Infrastructure.Persistence;
using Synap.Infrastructure.Persistence.Command;
using Synap.Infrastructure.Persistence.Data.Users;
using Synap.Infrastructure.Services.Auth;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<SynapDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            });

            if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
            else
            {
                options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            }
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<IUserReadRepository, UserReadRepository>();
        services.AddScoped<IUserWriteRepository, UserWriteRepository>();

        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddScoped<IApiTokenHasher, ApiTokenHasher>();

        // Web-layer kernel services that are provider-agnostic (unlike
        // SergioIzq.Infrastructure.Kernel's cache/email/Hangfire helpers, which are MySQL-only
        // and not used here - see design.md Decision 7).
        services.AddKernelPasswordHasher();
        services.AddKernelUserContext();

        return services;
    }
}
