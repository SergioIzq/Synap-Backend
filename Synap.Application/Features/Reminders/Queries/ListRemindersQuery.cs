using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Reminders.Queries;

/// <summary>
/// The "Recordatorios" section. <paramref name="TelegramConnected"/> is what the page needs to warn
/// that pending reminders won't arrive until Telegram is connected (specs/reminders "No chat linked").
/// </summary>
public sealed record RemindersResponse(
    IReadOnlyList<ReminderSummary> Reminders,
    bool TelegramConnected,
    int MaxTextLength);

public sealed record ListRemindersQuery : IQuery<RemindersResponse>;

public sealed class ListRemindersQueryHandler : IQueryHandler<ListRemindersQuery, RemindersResponse>
{
    private readonly IReminderReadRepository _reminderReadRepository;
    private readonly IUserReadRepository _userReadRepository;
    private readonly IUserContext _userContext;

    public ListRemindersQueryHandler(
        IReminderReadRepository reminderReadRepository, IUserReadRepository userReadRepository, IUserContext userContext)
    {
        _reminderReadRepository = reminderReadRepository;
        _userReadRepository = userReadRepository;
        _userContext = userContext;
    }

    public async Task<Result<RemindersResponse>> Handle(ListRemindersQuery request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var reminders = await _reminderReadRepository.ListPendingByUserAsync(userId, cancellationToken);
        var settings = await _userReadRepository.GetReminderSettingsAsync(userId, cancellationToken);

        return Result.Success(new RemindersResponse(reminders, settings?.TelegramConnected ?? false, Reminder.MaxTextLength));
    }
}

/// <summary>The reminders on one note, for the selector shown on the note itself.</summary>
public sealed record ListNoteRemindersQuery(Guid NoteId) : IQuery<IReadOnlyList<ReminderSummary>>;

public sealed class ListNoteRemindersQueryHandler : IQueryHandler<ListNoteRemindersQuery, IReadOnlyList<ReminderSummary>>
{
    private readonly IReminderReadRepository _reminderReadRepository;
    private readonly IUserContext _userContext;

    public ListNoteRemindersQueryHandler(IReminderReadRepository reminderReadRepository, IUserContext userContext)
    {
        _reminderReadRepository = reminderReadRepository;
        _userContext = userContext;
    }

    public async Task<Result<IReadOnlyList<ReminderSummary>>> Handle(ListNoteRemindersQuery request, CancellationToken cancellationToken)
    {
        var reminders = await _reminderReadRepository.ListByNoteAsync(_userContext.RequireUserId(), request.NoteId, cancellationToken);
        return Result.Success(reminders);
    }
}
