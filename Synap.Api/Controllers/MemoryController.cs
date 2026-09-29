using MediatR;
using Microsoft.AspNetCore.Mvc;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Memory.Commands;
using Synap.Application.Features.Memory.Queries;

namespace Synap.Api.Controllers;

/// <summary>
/// The user's assistant memory (specs/assistant-memory), shown and edited in Settings >
/// Memoria. Authenticated through the global FallbackPolicy; another user's entry id answers
/// 404 like a missing one.
/// </summary>
[ApiController]
[Route("api/memory")]
public class MemoryController : AbsController
{
    public MemoryController(ISender sender) : base(sender)
    {
    }

    [HttpGet]
    public async Task<IActionResult> List()
        => await SendAndHandleAsync(new ListMemoryQuery());

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] MemoryEntryRequest request)
        => await SendAndHandleAsync(new AddMemoryEntryCommand(request.Text));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] MemoryEntryRequest request)
        => await SendAndHandleAsync(new UpdateMemoryEntryCommand(id, request.Text));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
        => await SendAndHandleAsync(new DeleteMemoryEntryCommand(id));

    [HttpDelete]
    public async Task<IActionResult> DeleteAll()
        => await SendAndHandleAsync(new DeleteAllMemoryCommand());

    public sealed record MemoryEntryRequest(string? Text);
}
