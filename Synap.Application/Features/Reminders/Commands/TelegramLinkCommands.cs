using Microsoft.Extensions.Options;
using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Domain.Errors;
using Synap.Shared.Application;
using Synap.Shared.Application.Interfaces;
using System.Security.Cryptography;

namespace Synap.Application.Features.Reminders.Commands;

/// <summary>
/// specs/reminders "Connecting a Telegram account" (design.md Decision 5): the code is single use,
/// lives 15 minutes, and only ever links a chat - it never authenticates anyone into Synap, which
/// is why it can be shown on screen.
/// </summary>
public sealed record StartTelegramLinkCommand : ICommand<TelegramLinkInstructions>;

/// <summary><paramref name="Code"/> is returned once, at issue time; it is never read back.</summary>
public sealed record TelegramLinkInstructions(string Code, string BotUsername, DateTime ExpiresAtUtc);

public sealed class StartTelegramLinkCommandHandler : ICommandHandler<StartTelegramLinkCommand, TelegramLinkInstructions>
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    /// <summary>32 bytes of randomness, hex - 64 characters, the width of the column.</summary>
    private const int CodeBytes = 32;

    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly TelegramSettings _telegram;

    public StartTelegramLinkCommandHandler(
        IUserWriteRepository userWriteRepository, IUnitOfWork unitOfWork, IUserContext userContext, IOptions<TelegramSettings> telegram)
    {
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
        _telegram = telegram.Value;
    }

    public async Task<Result<TelegramLinkInstructions>> Handle(StartTelegramLinkCommand request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure<TelegramLinkInstructions>(UserErrors.NotFound);
        }

        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(CodeBytes));
        var expiresAt = DateTime.UtcNow.Add(Ttl);

        // Replaces any previous code: only the most recently issued one works.
        user.StartTelegramLink(code, expiresAt);
        // Explicit, like every other user write: the DbContext defaults to NoTracking outside
        // Development, so a fetched aggregate is not tracked (see AbsWriteRepository).
        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new TelegramLinkInstructions(code, _telegram.BotUsername, expiresAt));
    }
}

/// <summary>What Settings shows: connected or not, and the timezone reminders resolve in.</summary>
public sealed record TelegramStatus(bool Connected, string? Timezone);

public sealed record GetTelegramStatusQuery : IQuery<TelegramStatus>;

public sealed class GetTelegramStatusQueryHandler : IQueryHandler<GetTelegramStatusQuery, TelegramStatus>
{
    private readonly IUserReadRepository _userReadRepository;
    private readonly IUserContext _userContext;

    public GetTelegramStatusQueryHandler(IUserReadRepository userReadRepository, IUserContext userContext)
    {
        _userReadRepository = userReadRepository;
        _userContext = userContext;
    }

    public async Task<Result<TelegramStatus>> Handle(GetTelegramStatusQuery request, CancellationToken cancellationToken)
    {
        var settings = await _userReadRepository.GetReminderSettingsAsync(_userContext.RequireUserId(), cancellationToken);
        return Result.Success(new TelegramStatus(settings?.TelegramConnected ?? false, settings?.Timezone));
    }
}

/// <summary>
/// Run by the webhook when a chat sends `/start &lt;code&gt;`. Not a user-facing command: it runs
/// unauthenticated, so the code itself is the only thing that identifies the account, and an
/// unknown, expired or used code reveals nothing about any user (specs/reminders "Unknown code").
/// </summary>
public sealed record CompleteTelegramLinkCommand(string? Code, string ChatId) : ICommand<bool>;

public sealed class CompleteTelegramLinkCommandHandler : ICommandHandler<CompleteTelegramLinkCommand, bool>
{
    private readonly IUserReadRepository _userReadRepository;
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;

    public CompleteTelegramLinkCommandHandler(
        IUserReadRepository userReadRepository, IUserWriteRepository userWriteRepository, IUnitOfWork unitOfWork)
    {
        _userReadRepository = userReadRepository;
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
    }

    /// <summary>True when a chat was linked; false for an unknown, expired or already-used code.</summary>
    public async Task<Result<bool>> Handle(CompleteTelegramLinkCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.ChatId))
        {
            return Result.Success(false);
        }

        var owner = await _userReadRepository.GetByTelegramLinkTokenAsync(request.Code.Trim(), cancellationToken);
        if (owner is null)
        {
            return Result.Success(false);
        }

        var user = await _userWriteRepository.GetByIdAsync(owner.Id.Value, cancellationToken);
        if (user is null || !user.HasValidTelegramLink(DateTime.UtcNow))
        {
            return Result.Success(false);
        }

        user.CompleteTelegramLink(request.ChatId);
        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }
}

/// <summary>
/// specs/reminders "Disconnecting a Telegram account": reminders are left pending, so connecting
/// again picks them straight back up.
/// </summary>
public sealed record DisconnectTelegramCommand : ICommand;

public sealed class DisconnectTelegramCommandHandler : ICommandHandler<DisconnectTelegramCommand>
{
    private readonly IUserWriteRepository _userWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public DisconnectTelegramCommandHandler(
        IUserWriteRepository userWriteRepository, IUnitOfWork unitOfWork, IUserContext userContext)
    {
        _userWriteRepository = userWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result> Handle(DisconnectTelegramCommand request, CancellationToken cancellationToken)
    {
        var user = await _userWriteRepository.GetByIdAsync(_userContext.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserErrors.NotFound);
        }

        user.DisconnectTelegram();
        _userWriteRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
