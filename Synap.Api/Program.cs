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
using Synap.Infrastructure;
using Synap.Shared.Application;
using System.Threading.RateLimiting;

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

    // No connection string passed: AddKernelHealthChecks probes MySQL specifically (see
    // design.md Decision 7 amendment) - not usable against Postgres.
    builder.Services.AddKernelHealthChecks();

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

        options.AddPolicy(RateLimitPolicies.AiHeavy, httpContext =>
        {
            var userId = httpContext.User.FindFirst("sub")?.Value ?? "anonymous";

            return RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        });
    });

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    // Own middleware instead of the kernel's UseGlobalExceptionHandler (MySQL-specific
    // exception mapping - see design.md Decision 7 amendment); UseResultHandler/UseNoCache are
    // provider-agnostic and are reused as-is.
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseResultHandler();
    app.UseNoCache();

    app.UseRateLimiter();

    app.UseCors(app.Environment.IsDevelopment() ? "LocalhostPolicy" : "ProductionPolicy");

    app.UseStaticFiles(); // Swagger UI's CSS/JS
    app.UseCookiePolicy();

    app.UseResponseCompression();

    app.UseKernelSwaggerUI("Synap API v1"); // Development only

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHealthChecks("/health");

    app.MapGet("/", () => Results.Redirect("/swagger"));

    Log.Information("Starting server...");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Fatal error during startup");
}
finally
{
    await Log.CloseAndFlushAsync();
}
