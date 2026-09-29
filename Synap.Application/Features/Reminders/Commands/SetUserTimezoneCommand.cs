using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Reminders.Commands;

/// <summary>
/// Stores the IANA timezone the frontend sends with a question or a reminder (design.md Context),
/// last one wins. Needed because a snooze is pressed from Telegram, where nothing can send it.
///
/// Never fails the request that carried it: a missing, unknown or unchanged timezone is a no-op, so
/// asking the assistant never breaks over a timezone the server doesn't recognise.
/// </summary>
public sealed record SetUserTimezoneCommand(string? Timezone) : ICommand;

public sealed class SetUserTimezoneCommandHandler : ICommandHandler<SetUserTimezoneCommand>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public SetUserTimezoneCommandHandler(
        IUserWriteRepository userWriteRepository, IUnitOfWork unitOfWork, IUserContext userContext)
    {
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result> Handle(SetUserTimezoneCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Timezone))
        {
            return Result.Success();
        }

        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null || user.Timezone == request.Timezone.Trim())
        {
            return Result.Success();
        }

        // A value this machine doesn't know is ignored rather than stored: it would otherwise
        // silently shift every future reminder.
        if (user.SetTimezone(request.Timezone))
        {
            _userWriteRepository.Update(user);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}
