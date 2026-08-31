using MediatR;
using Microsoft.AspNetCore.Mvc;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Assistant.Queries;

namespace Synap.Api.Controllers;

[ApiController]
[Route("api/assistant")]
public class AssistantController : AbsController
{
    public AssistantController(ISender sender) : base(sender)
    {
    }

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest request)
        => await SendAndHandleAsync(new AskAssistantQuery(request.Question));

    public sealed record AskRequest(string Question);
}
