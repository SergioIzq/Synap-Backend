using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Synap.Api.Health;

/// <summary>
/// { status, checks: { database, aiService } } - check names and statuses only, never
/// exception messages or connection details (specs/platform-operations "without exposing
/// secrets or user data").
/// </summary>
public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status.ToString()),
        });
    }
}
