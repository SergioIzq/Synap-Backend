using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Settings.Commands;
using Synap.Application.Features.Settings.Queries;

namespace Synap.Api.Controllers;

/// <summary>
/// The user's own settings (specs/user-settings). Authenticated through the global
/// FallbackPolicy like every other controller. The routes that reach Groq (saving a key,
/// listing or choosing a model) share the AI-heavy rate limit, since each one is an outbound
/// call made with the user's key. The personal access token keeps its existing
/// /api/auth/api-token routes.
/// </summary>
[ApiController]
[Route("api/settings")]
public class SettingsController : AbsController
{
    public SettingsController(ISender sender) : base(sender)
    {
    }

    [HttpGet]
    public async Task<IActionResult> Get()
        => await SendAndHandleAsync(new GetSettingsQuery());

    [HttpPut("ai/groq-key")]
    [EnableRateLimiting(RateLimitPolicies.AiHeavy)]
    public async Task<IActionResult> SaveGroqKey([FromBody] SaveGroqKeyRequest request)
        => await SendAndHandleAsync(new SaveGroqApiKeyCommand(request.ApiKey));

    [HttpDelete("ai/groq-key")]
    public async Task<IActionResult> DeleteGroqKey()
        => await SendAndHandleAsync(new DeleteGroqApiKeyCommand());

    [HttpGet("ai/models")]
    [EnableRateLimiting(RateLimitPolicies.AiHeavy)]
    public async Task<IActionResult> ListModels()
        => await SendAndHandleAsync(new ListGroqModelsQuery());

    [HttpPut("ai/model")]
    [EnableRateLimiting(RateLimitPolicies.AiHeavy)]
    public async Task<IActionResult> SetModel([FromBody] SetModelRequest request)
        => await SendAndHandleAsync(new SetGroqModelCommand(request.Model));

    public sealed record SaveGroqKeyRequest(string ApiKey);

    public sealed record SetModelRequest(string? Model);
}
