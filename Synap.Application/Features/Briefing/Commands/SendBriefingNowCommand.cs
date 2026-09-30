using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Briefing.Commands;

/// <summary>
/// Sends the requesting user their briefing now (specs/briefing "A briefing can be asked for at
/// any moment"). It is the same briefing the sweep would send, built from their data as it stands.
///
/// Deliberately does not touch the resolved day: asking is not the same as being sent one, so the
/// automatic briefing still arrives at its hour (daily-briefing design.md Decision 7). It does not
/// require the briefing to be turned on either - reading your own notes is not consent to a daily
/// message.
/// </summary>
public sealed record SendBriefingNowCommand : ICommand;

public sealed class SendBriefingNowCommandHandler : ICommandHandler<SendBriefingNowCommand>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly BriefingDispatcher _dispatcher;
    private readonly IUserContext _userContext;

    public SendBriefingNowCommandHandler(
        IUserWriteRepository userWriteRepository, BriefingDispatcher dispatcher, IUserContext userContext)
    {
        _userWriteRepository = userWriteRepository;
        _dispatcher = dispatcher;
        _userContext = userContext;
    }

    public async Task<Result> Handle(SendBriefingNowCommand request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure(BriefingErrors.UserNotFound);
        }

        // answerWhenEmpty: true - they pressed something, so they get an answer either way.
        var result = await _dispatcher.SendAsync(user, DateTime.UtcNow, answerWhenEmpty: true, cancellationToken);

        return result switch
        {
            BriefingDispatchResult.Sent => Result.Success(),
            BriefingDispatchResult.NoChatLinked => Result.Failure(BriefingErrors.TelegramNotConnected),
            _ => Result.Failure(BriefingErrors.NotDelivered),
        };
    }
}
