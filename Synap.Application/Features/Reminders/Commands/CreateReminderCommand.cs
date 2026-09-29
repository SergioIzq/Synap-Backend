using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Application.Features.Reminders.Commands;

/// <summary>
/// specs/reminders - used by the "Recordatorios" section, by the selector on a note, and by the
/// assistant's `set_reminder` action alike, so all three get the same validation and ownership.
/// <paramref name="DueAtUtc"/> is an instant in UTC: the caller (the web app, or the model through
/// the agent) resolves the user's wording against their timezone first (design.md Decision 6).
/// </summary>
public sealed record CreateReminderCommand(
    string? Text,
    DateTime DueAtUtc,
    Guid? NoteId = null,
    string? Recurrence = null) : ICommand<ReminderSummary>;

public sealed class CreateReminderCommandHandler : ICommandHandler<CreateReminderCommand, ReminderSummary>
{
    private readonly IReminderWriteRepository _reminderWriteRepository;
    private readonly INoteReadRepository _noteReadRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public CreateReminderCommandHandler(
        IReminderWriteRepository reminderWriteRepository,
        INoteReadRepository noteReadRepository,
        IUnitOfWork unitOfWork,
        IUserContext userContext)
    {
        _reminderWriteRepository = reminderWriteRepository;
        _noteReadRepository = noteReadRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result<ReminderSummary>> Handle(CreateReminderCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        string? noteTitle = null;
        NoteId? noteId = null;
        if (request.NoteId is { } requestedNoteId)
        {
            // Another user's note is exactly a missing one (specs/reminders "Reminders never cross users").
            var note = await _noteReadRepository.GetByIdAsync(userId, requestedNoteId, cancellationToken);
            if (note is null)
            {
                return Result.Failure<ReminderSummary>(Error.NotFound("Nota no encontrada."));
            }

            noteId = NoteId.CreateFromDatabase(requestedNoteId);
            noteTitle = note.Title;
        }

        var reminder = Reminder.Create(
            UserId.CreateFromDatabase(userId), request.Text, request.DueAtUtc, DateTime.UtcNow, noteId, request.Recurrence);
        if (reminder.IsFailure)
        {
            return Result.Failure<ReminderSummary>(reminder.Error);
        }

        await _reminderWriteRepository.CreateAsync(reminder.Value, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(ReminderSummary.From(reminder.Value, noteTitle));
    }
}
