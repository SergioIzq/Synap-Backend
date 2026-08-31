using System.Net;
using System.Text.Json;

namespace Synap.Api.Middleware;

/// <summary>
/// Replaces the kernel's GlobalExceptionHandler, which maps MySqlException specifically (see
/// design.md Decision 7 amendment) - this just turns any unhandled exception into a generic
/// JSON 500 instead of the default ASP.NET Core error page. Application-level failures (bad
/// input, not found, duplicate email, ...) are expected to be modeled as a failed Result and
/// never reach this middleware - see AbsController.HandleResult/SendAndHandleAsync.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "An unexpected error occurred." }));
        }
    }
}
