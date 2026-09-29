using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Reminders.Commands;

/// <summary>
/// specs/reminders "Managing reminders from the web app" - the same validation as creation, so an
/// edit can't leave a reminder in a state a creation would have refused. A reminder that is
/// missing or someone else's is reported as missing, never as forbidden.
/// </summary>
public sealed record UpdateReminderCommand(
    Guid ReminderId,
    string? Text,
    DateTime DueAtUtc,
    string? Recurrence = null) : ICommand;

public sealed class UpdateReminderCommandHandler : ICommandHandler<UpdateReminderCommand>
{
    private readonly IReminderWriteRepository _reminderWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public UpdateReminderCommandHandler(
        IReminderWriteRepository reminderWriteRepository, IUnitOfWork unitOfWork, IUserContext userContext)
    {
        _reminderWriteRepository = reminderWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result> Handle(UpdateReminderCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var reminder = await _reminderWriteRepository.GetOwnedByUserAsync(request.ReminderId, userId, cancellationToken);
        if (reminder is null)
        {
            return Result.Failure(Reminder.NotFound);
        }

        var edited = reminder.Edit(request.Text, request.DueAtUtc, DateTime.UtcNow, request.Recurrence);
        if (edited.IsFailure)
        {
            return edited;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
