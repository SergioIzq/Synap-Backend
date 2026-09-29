using MediatR;
using Microsoft.AspNetCore.Mvc;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Reminders.Commands;
using Synap.Application.Features.Reminders.Queries;

namespace Synap.Api.Controllers;

/// <summary>
/// The user's reminders (specs/reminders), shown in the "Recordatorios" section and on a note.
/// Authenticated through the global FallbackPolicy; another user's reminder id answers 404 like a
/// missing one.
///
/// <c>dueAtUtc</c> is an instant in UTC - the web app resolves the user's wording and its own
/// selector against the browser's timezone before calling. <c>timezone</c> is that browser
/// timezone, stored so a snooze pressed later in Telegram knows what "09:00 tomorrow" means
/// (assistant-reminders design.md Context).
/// </summary>
[ApiController]
[Route("api/reminders")]
public class RemindersController : AbsController
{
    public RemindersController(ISender sender) : base(sender)
    {
    }

    [HttpGet]
    public async Task<IActionResult> List()
        => await SendAndHandleAsync(new ListRemindersQuery());

    [HttpGet("note/{noteId:guid}")]
    public async Task<IActionResult> ForNote(Guid noteId)
        => await SendAndHandleAsync(new ListNoteRemindersQuery(noteId));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReminderRequest request)
    {
        await RememberTimezoneAsync(request.Timezone);
        return await SendAndHandleAsync(
            new CreateReminderCommand(request.Text, request.DueAtUtc, request.NoteId, request.Recurrence));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateReminderRequest request)
    {
        await RememberTimezoneAsync(request.Timezone);
        return await SendAndHandleAsync(new UpdateReminderCommand(id, request.Text, request.DueAtUtc, request.Recurrence));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id)
        => await SendAndHandleAsync(new CancelReminderCommand(id));

    /// <summary>Never fails the request: an unknown or missing timezone is simply not stored.</summary>
    private async Task RememberTimezoneAsync(string? timezone)
    {
        if (!string.IsNullOrWhiteSpace(timezone))
        {
            await _sender.Send(new SetUserTimezoneCommand(timezone));
        }
    }

    public sealed record CreateReminderRequest(
        string? Text,
        DateTime DueAtUtc,
        Guid? NoteId = null,
        string? Recurrence = null,
        string? Timezone = null);

    public sealed record UpdateReminderRequest(
        string? Text,
        DateTime DueAtUtc,
        string? Recurrence = null,
        string? Timezone = null);
}
