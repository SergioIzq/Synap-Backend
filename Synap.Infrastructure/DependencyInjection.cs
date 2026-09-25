using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SergioIzq.AspNetCore.Kernel.DependencyInjection;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Infrastructure.BackgroundJobs;
using Synap.Infrastructure.Persistence;
using Synap.Infrastructure.Persistence.Command;
using Synap.Infrastructure.Persistence.Data.Notes;
using Synap.Infrastructure.Persistence.Data.Tags;
using Synap.Infrastructure.Persistence.Data.Users;
using Synap.Infrastructure.Services.Ai;
using Synap.Infrastructure.Services.Auth;
using Synap.Infrastructure.Services.Bookmarks;
using Synap.Infrastructure.Services.Email;
using Synap.Infrastructure.Services.Secrets;
using Synap.Shared.Application.BackgroundJobs;
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
        services.AddScoped<IDbConnectionFactory, NpgsqlConnectionFactory>();

        services.AddScoped<IUserReadRepository, UserReadRepository>();
        services.AddScoped<IUserWriteRepository, UserWriteRepository>();

        services.AddScoped<INoteReadRepository, NoteReadRepository>();
        services.AddScoped<INoteWriteRepository, NoteWriteRepository>();
        services.AddScoped<ITagWriteRepository, TagWriteRepository>();

        // Built eagerly: a missing or too-short JWT_SECRET_KEY stops the API at startup with a
        // clear message instead of failing every login with a 500.
        services.AddSingleton<IJwtTokenGenerator>(new JwtTokenGenerator(configuration));
        services.AddScoped<IApiTokenHasher, ApiTokenHasher>();
        services.AddMemoryCache();

        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));
        services.AddSingleton<ISmtpTransport, MailKitSmtpTransport>();
        services.AddSingleton<IEmailSender, BackgroundEmailSender>();
        services.AddSingleton<IRecoveryRequestLimiter, MemoryRecoveryRequestLimiter>();
        services.AddSingleton<IUserSessionCache, UserSessionCache>();

        // Built eagerly (not in a factory lambda) so a missing/invalid SECRETS_ENCRYPTION_KEY
        // stops the API at startup instead of on the first user saving a Groq key.
        services.AddSingleton<ISecretProtector>(AesGcmSecretProtector.FromBase64(configuration["Secrets:EncryptionKey"]));

        // In-process background queue (design.md Decision 8) - singleton so the hosted service
        // and every request-scoped IBackgroundJobQueue consumer share the same channel.
        services.AddSingleton<BackgroundJobQueue>();
        services.AddSingleton<IBackgroundJobQueue>(sp => sp.GetRequiredService<BackgroundJobQueue>());
        services.AddHostedService<QueuedJobHostedService>();

        services.AddHttpClient<IBookmarkMetadataScraper, BookmarkMetadataScraper>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SynapBot/1.0 (+https://synap.sergioizq.com)");
        });

        services.AddHttpClient<IAiServiceClient, AiServiceClient>(client =>
        {
            var baseUrl = configuration["AiService:BaseUrl"]
                ?? throw new InvalidOperationException("Missing 'AiService:BaseUrl' configuration.");
            var internalApiKey = configuration["AiService:InternalApiKey"]
                ?? throw new InvalidOperationException("Missing 'AiService:InternalApiKey' configuration.");

            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(25); // generous: a Groq round trip can take a few seconds.
            client.DefaultRequestHeaders.Add("X-Internal-Api-Key", internalApiKey);
        });

        // Web-layer kernel services that are provider-agnostic (unlike
        // SergioIzq.Infrastructure.Kernel's cache/email/Hangfire helpers, which are MySQL-only
        // and not used here - see design.md Decision 7).
        services.AddKernelPasswordHasher();
        services.AddKernelUserContext();

        return services;
    }
}
