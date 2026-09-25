using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using SergioIzq.AspNetCore.Kernel.DependencyInjection;
using SergioIzq.AspNetCore.Kernel.Middleware;
using SergioIzq.Logging.HtmlFile;
using Serilog;
using Synap.Api;
using Synap.Api.Authentication;
using Synap.Api.Middleware;
using Synap.Application;
using Synap.Application.Features.Settings;
using Microsoft.EntityFrameworkCore;
using Synap.Infrastructure;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Application;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Synap.Api.Health;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Shared.Application.Interfaces;
using System.Net;
using System.Security.Claims;

// AbsEntity (SergioIzq.Domain.Kernel) sets FechaCreacion via DateTime.Now (Kind=Local).
// Npgsql 6+ rejects Kind=Local on timestamptz without this switch.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

const string ApiTokenSchemeName = "ApiToken";
const string SmartBearerSchemeName = "SmartBearer";

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

Log.Information("Starting Synap API...");

BootstrapExtensions.SetKernelCulture("es-ES");

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.UseKernelSerilog("Synap");
    builder.WebHost.ConfigureKernelKestrel();

    // Kernel bootstrap: CORS, JSON (MVC, reflection-based - no source-gen JSON context needed
    // at this scale), model validation, Swagger with Bearer, compression, cookie policy.
    builder.Services.AddKernelCors("synap.sergioizq.com");
    builder.Services.AddKernelJsonOptions();
    builder.Services.AddKernelControllers();

    // Enums as strings ("text"/"codeSnippet"/"bookmark"), not raw numbers - additive to
    // whatever AddKernelControllers/AddKernelJsonOptions already configured.
    builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

    builder.Services.AddKernelModelValidation();
    builder.Services.AddKernelSwagger();
    builder.Services.AddKernelResponseCompression();
    builder.Services.AddKernelCookiePolicy(builder.Environment);

    builder.Services.AddHtmlFileLogging(opts => builder.Configuration.GetSection("HtmlFileLog").Bind(opts));

    builder.Services.AddApplication();
    builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));
    builder.Services.AddSharedApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddKernelJwtAuthentication(builder.Configuration);

    // Personal-access-token scheme + a policy scheme that routes between it and the kernel's
    // session JWT depending on the shape of the bearer token (a JWT always has 3 dot-separated
    // segments; the API token, base64url, does not). See specs/identity and design.md Decision 3.
    builder.Services.AddAuthentication()
        .AddScheme<ApiTokenAuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenSchemeName, _ => { })
        .AddPolicyScheme(SmartBearerSchemeName, "Session JWT or personal API token", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var authHeader = context.Request.Headers.Authorization.ToString();

                if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return JwtBearerDefaults.AuthenticationScheme;
                }

                var token = authHeader["Bearer ".Length..].Trim();
                var isJwtShaped = token.Count(c => c == '.') == 2;

                return isJwtShaped ? JwtBearerDefaults.AuthenticationScheme : ApiTokenSchemeName;
            };
        });

    builder.Services.Configure<AuthenticationOptions>(options =>
    {
        options.DefaultAuthenticateScheme = SmartBearerSchemeName;
        options.DefaultChallengeScheme = SmartBearerSchemeName;
    });

    // specs/platform-operations "Health reporting": Postgres down = Unhealthy (nothing works),
    // AI service down = Degraded (notes still work). Short timeouts so /health itself stays fast.
    builder.Services.AddHttpClient(AiServiceHealthCheck.HttpClientName, client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["AiService:BaseUrl"]
            ?? throw new InvalidOperationException("Missing 'AiService:BaseUrl' configuration."));
        client.Timeout = TimeSpan.FromSeconds(3);
    });

    builder.Services.AddHealthChecks()
        .AddNpgSql(
            builder.Configuration.GetConnectionString("DefaultConnection")!,
            name: "database",
            failureStatus: HealthStatus.Unhealthy,
            timeout: TimeSpan.FromSeconds(3))
        .AddCheck<AiServiceHealthCheck>("aiService", failureStatus: HealthStatus.Degraded, timeout: TimeSpan.FromSeconds(4));

    // Every endpoint requires authentication unless explicitly [AllowAnonymous] (register/login
    // today; knowledge-vault and ai-assistant controllers land already covered by this default -
    // see specs/identity "Authenticated access to the vault").
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    // Per-user limit on the AI-heavy endpoints only (quick-capture triggers an embedding job;
    // the assistant chat calls an external LLM) - open registration means anyone with the link
    // can create an account, and this is what keeps one user from starving the shared VPS or
    // burning through the external LLM's free-tier quota (design.md Risks, specs/ai-assistant).
    // Numbers are a conservative starting point, not tuned against real usage yet (design.md
    // Open Questions) - partitioned by user ID from the JWT/API-token claims, not IP, since the
    // concern is per-account abuse, not per-network.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Same Result shape as every other error, so the web app shows the Spanish message.
        options.OnRejected = async (context, cancellationToken) =>
            await context.HttpContext.Response.WriteAsJsonAsync(
                Result.Failure(Error.Validation(RateLimitPolicies.RejectionMessage)), cancellationToken);

        options.AddPolicy(RateLimitPolicies.AiHeavy, httpContext =>
        {
            // The JWT handler may map "sub" to NameIdentifier; the API-token handler emits both.
            var userId = httpContext.User.FindFirst("sub")?.Value
                ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? "anonymous";

            return RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        });

        // Brute-force protection on the anonymous endpoints (specs/identity "Authentication
        // attempt limiting"): per client IP, which is only the real one once UseForwardedHeaders
        // has run with the proxy in KnownProxies - otherwise every client shares the proxy's IP.
        options.AddPolicy(RateLimitPolicies.Auth, httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(ClientIp(httpContext), _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = RateLimitPolicies.AuthPermitLimit,
                Window = RateLimitPolicies.AuthWindow,
                SegmentsPerWindow = 6,
                QueueLimit = 0,
            }));

        options.AddPolicy(RateLimitPolicies.Register, httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(ClientIp(httpContext), _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = RateLimitPolicies.RegisterPermitLimit,
                Window = RateLimitPolicies.RegisterWindow,
                SegmentsPerWindow = 6,
                QueueLimit = 0,
            }));
    });

    // Behind the VPS reverse proxy RemoteIpAddress is the proxy's; X-Forwarded-For is only
    // trusted when it comes from an address listed in ForwardedHeaders:KnownProxies
    // (comma-separated) - trusting it from anyone would let a client pick its own IP and dodge
    // the auth rate limit. See docs/deployment-runbook.md.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        var knownProxies = builder.Configuration["ForwardedHeaders:KnownProxies"] ?? string.Empty;
        foreach (var proxy in knownProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }
    });

    // A session JWT stays cryptographically valid after its account is deleted - reject it if
    // the user no longer exists (backend-hardening design.md Decision 2). Cached for a minute.
    builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.Events ??= new JwtBearerEvents();
        var previous = options.Events.OnTokenValidated;

        options.Events.OnTokenValidated = async context =>
        {
            await previous(context);
            if (context.Result is not null)
            {
                return;
            }

            var subject = context.Principal?.FindFirst("sub")?.Value
                ?? context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            var cache = context.HttpContext.RequestServices.GetRequiredService<IUserExistenceCache>();
            if (!Guid.TryParse(subject, out var userId) || !await cache.ExistsAsync(userId, context.HttpContext.RequestAborted))
            {
                context.Fail("La cuenta ya no existe.");
            }
        };
    });

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SynapDbContext>();
        db.Database.Migrate();
    }

    // First, so logging, rate limiting and everything else see the real client IP.
    app.UseForwardedHeaders();

    app.UseSerilogRequestLogging();

    // Own middleware instead of the kernel's UseGlobalExceptionHandler (MySQL-specific
    // exception mapping - see design.md Decision 7 amendment); UseResultHandler/UseNoCache are
    // provider-agnostic and are reused as-is.
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseResultHandler();
    app.UseNoCache();

    app.UseCors(app.Environment.IsDevelopment() ? "LocalhostPolicy" : "ProductionPolicy");

    app.UseStaticFiles(); // Swagger UI's CSS/JS
    app.UseCookiePolicy();

    app.UseResponseCompression();

    app.UseKernelSwaggerUI("Synap API v1"); // Development only

    app.UseAuthentication();
    app.UseAuthorization();

    // After authentication: the AiHeavy policy partitions by the user's claims, which don't
    // exist yet before UseAuthentication (every user used to share one "anonymous" bucket).
    app.UseRateLimiter();

    app.MapControllers();
    app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.WriteAsync })
        .AllowAnonymous();

    app.MapGet("/", () => Results.Redirect("/swagger"));

    Log.Information("Starting server...");
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Fatal error during startup");
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string ClientIp(HttpContext httpContext) => httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

/// <summary>Exposed for WebApplicationFactory in Synap.IntegrationTests.</summary>
public partial class Program;
