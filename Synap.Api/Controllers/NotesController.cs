using MediatR;
using Microsoft.AspNetCore.Mvc;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Notes.Commands.AddTag;
using Synap.Application.Features.Notes.Commands.Create;
using Synap.Application.Features.Notes.Commands.Delete;
using Synap.Application.Features.Notes.Commands.QuickCapture;
using Synap.Application.Features.Notes.Commands.Update;
using Synap.Application.Features.Notes.Queries;

namespace Synap.Api.Controllers;

/// <summary>
/// No [Authorize] needed on any action here: the global FallbackPolicy (Program.cs) already
/// requires authentication on every endpoint, and the SmartBearer scheme transparently accepts
/// either the session JWT or the personal access token - quick-capture works through the same
/// endpoint shape as everything else, distinguished only by which kind of Bearer the caller sends.
/// </summary>
[ApiController]
[Route("api/notes")]
public class NotesController : AbsController
{
    public NotesController(ISender sender) : base(sender)
    {
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateNoteCommand command)
        => await SendAndHandleAsync(command);

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateNoteRequest request)
        => await SendAndHandleAsync(new UpdateNoteCommand(id, request.Title, request.Content));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
        => await SendAndHandleAsync(new DeleteNoteCommand(id));

    [HttpPost("{id:guid}/tags")]
    public async Task<IActionResult> AddTag(Guid id, [FromBody] AddTagRequest request)
        => await SendAndHandleAsync(new AddTagCommand(id, request.TagName));

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] string? tag)
        => await SendAndHandleAsync(new SearchNotesQuery(q, tag));

    /// <summary>Used by the iOS Shortcut, authenticated with the personal access token (design.md Decision 3).</summary>
    [HttpPost("quick-capture")]
    public async Task<IActionResult> QuickCapture([FromBody] QuickCaptureCommand command)
        => await SendAndHandleAsync(command);

    public sealed record UpdateNoteRequest(string? Title, string Content);

    public sealed record AddTagRequest(string TagName);
}
