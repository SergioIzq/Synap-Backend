using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Synap.Api.Health;

/// <summary>
/// Pings the AI service's own /health. Registered as Degraded rather than Unhealthy
/// (specs/platform-operations): notes keep working without it, only the assistant and
/// related notes don't.
/// </summary>
public sealed class AiServiceHealthCheck : IHealthCheck
{
    public const string HttpClientName = "ai-service-health";

    private readonly IHttpClientFactory _httpClientFactory;

    public AiServiceHealthCheck(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClientFactory.CreateClient(HttpClientName).GetAsync("/health", cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Unreachable");
        }
    }
}
