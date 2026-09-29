using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Assistant.Queries;
using Synap.Application.Features.Reminders.Commands;
using Synap.Domain;

namespace Synap.Api.Controllers;

[ApiController]
[Route("api/assistant")]
public class AssistantController : AbsController
{
    public AssistantController(ISender sender) : base(sender)
    {
    }

    [HttpPost("ask")]
    [EnableRateLimiting(RateLimitPolicies.AiHeavy)]
    public async Task<IActionResult> Ask([FromBody] AskRequest request)
    {
        // Remembered before the question is answered, so a snooze pressed later in Telegram knows
        // what "09:00 tomorrow" means. Never fails the question: an unknown value is not stored.
        if (!string.IsNullOrWhiteSpace(request.Timezone))
        {
            await _sender.Send(new SetUserTimezoneCommand(request.Timezone));
        }

        return await SendAndHandleAsync(new AskAssistantQuery(request.Question, request.Scope, request.History, request.Timezone));
    }

    /// <summary>
    /// <c>scope</c> (<c>{ noteId }</c> or <c>{ tag }</c>) and <c>history</c> are optional
    /// (scoped-assistant), as is <c>timezone</c>, the browser's IANA timezone that reminder moments
    /// are resolved against (assistant-reminders) - older clients sending only <c>question</c>
    /// behave as before.
    /// </summary>
    public sealed record AskRequest(
        string Question,
        AssistantScope? Scope = null,
        IReadOnlyList<AssistantTurn>? History = null,
        string? Timezone = null);
}
