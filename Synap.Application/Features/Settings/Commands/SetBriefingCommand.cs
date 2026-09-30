using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Settings.Commands;

/// <summary>
/// Turns the morning briefing on or off for the requesting user, and sets the local hour it
/// arrives (specs/briefing "The briefing is off until the user turns it on"). Turning it off
/// leaves <paramref name="Hour"/> unused, and turning it back on without one keeps the hour
/// already chosen.
/// </summary>
public sealed record SetBriefingCommand(bool Enabled, int? Hour) : ICommand<BriefingSettingsResponse>;

public sealed class SetBriefingCommandHandler : ICommandHandler<SetBriefingCommand, BriefingSettingsResponse>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public SetBriefingCommandHandler(IUserWriteRepository userWriteRepository, IUnitOfWork unitOfWork, IUserContext userContext)
    {
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result<BriefingSettingsResponse>> Handle(SetBriefingCommand request, CancellationToken cancellationToken)
    {
        // The requesting user and no other: the id comes from the token, never from the request
        // (specs/briefing "One user's setting is not another's").
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure<BriefingSettingsResponse>(SettingsErrors.UserNotFound);
        }

        if (!user.SetBriefing(request.Enabled, request.Hour))
        {
            return Result.Failure<BriefingSettingsResponse>(SettingsErrors.BriefingHourInvalid);
        }

        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(BriefingSettingsResponse.From(user));
    }
}
