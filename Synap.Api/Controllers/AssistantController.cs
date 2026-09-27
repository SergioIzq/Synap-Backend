using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Assistant.Queries;
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
        => await SendAndHandleAsync(new AskAssistantQuery(request.Question, request.Scope, request.History));

    /// <summary>
    /// <c>scope</c> (<c>{ noteId }</c> or <c>{ tag }</c>) and <c>history</c> are optional
    /// (scoped-assistant) - older clients sending only <c>question</c> behave as before.
    /// </summary>
    public sealed record AskRequest(string Question, AssistantScope? Scope = null, IReadOnlyList<AssistantTurn>? History = null);
}
