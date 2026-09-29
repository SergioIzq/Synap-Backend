using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Reminders.Commands;

/// <summary>
/// specs/reminders "Reminder cancelled": it disappears from the list and is never delivered, so the
/// row goes rather than being marked - there is no history of reminders to keep (design.md Decision 2).
/// </summary>
public sealed record CancelReminderCommand(Guid ReminderId) : ICommand;

public sealed class CancelReminderCommandHandler : ICommandHandler<CancelReminderCommand>
{
    private readonly IReminderWriteRepository _reminderWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public CancelReminderCommandHandler(
        IReminderWriteRepository reminderWriteRepository, IUnitOfWork unitOfWork, IUserContext userContext)
    {
        _reminderWriteRepository = reminderWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result> Handle(CancelReminderCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var reminder = await _reminderWriteRepository.GetOwnedByUserAsync(request.ReminderId, userId, cancellationToken);
        if (reminder is null)
        {
            return Result.Failure(Reminder.NotFound);
        }

        _reminderWriteRepository.Delete(reminder);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
