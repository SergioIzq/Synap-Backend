using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Users;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Reminders;

/// <summary>
/// Delivers the reminders that have fallen due (specs/reminders "Pending reminders are delivered
/// when they fall due"). Called by the poller each tick; separated from it so the delivery rules
/// are testable without a host.
///
/// A reminder is marked as delivered only once Telegram has accepted it: a failure leaves it
/// pending, so the next tick tries again, and one failure never stops the others
/// (specs/reminders "Delivery failures do not lose a reminder").
/// </summary>
public sealed class ReminderDeliveryService
{
    /// <summary>A tick delivers at most this many, so one backlog can't hold the poller for minutes.</summary>
    public const int MaxPerTick = 50;

    private readonly IReminderWriteRepository _reminderWriteRepository;
    private readonly ITelegramSender _telegramSender;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AppOptions _appOptions;
    private readonly WithheldReminderRecorder _withheld;
    private readonly ILogger<ReminderDeliveryService> _logger;

    public ReminderDeliveryService(
        IReminderWriteRepository reminderWriteRepository,
        ITelegramSender telegramSender,
        IUnitOfWork unitOfWork,
        IOptions<AppOptions> appOptions,
        WithheldReminderRecorder withheld,
        ILogger<ReminderDeliveryService> logger)
    {
        _reminderWriteRepository = reminderWriteRepository;
        _telegramSender = telegramSender;
        _unitOfWork = unitOfWork;
        _appOptions = appOptions.Value;
        _withheld = withheld;
        _logger = logger;
    }

    /// <summary>
    /// Delivers everything due at <paramref name="nowUtc"/> and returns how many went out.
    /// <paramref name="betweenSends"/> is awaited between messages (design.md Decision 4: a few
    /// seconds, so a backlog doesn't hit Telegram's rate limit); the caller passes it so tests
    /// don't wait.
    /// </summary>
    public async Task<int> DeliverDueAsync(
        DateTime nowUtc, Func<CancellationToken, Task>? betweenSends = null, CancellationToken cancellationToken = default)
    {
        // Nothing is read, let alone delivered, while delivery is turned off (specs/reminders
        // "Reminders are inert while delivery is turned off").
        if (!_telegramSender.IsEnabled)
        {
            return 0;
        }

        var due = await _reminderWriteRepository.ListDueAsync(nowUtc, MaxPerTick, cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        var delivered = 0;
        var first = true;
        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.TelegramChatId))
            {
                // No chat linked: it stays pending for whenever one is (specs/reminders "No chat
                // linked"). Said once per reminder rather than on every tick, which is why this
                // used to say nothing at all.
                if (_withheld.ShouldRecord(item.Reminder.Id.Value))
                {
                    _logger.LogWarning(
                        "Reminder {ReminderId} of user {UserId} fell due and was withheld: the user has no linked Telegram chat",
                        item.Reminder.Id.Value, item.Reminder.UserId.Value);
                }

                continue;
            }

            if (!first && betweenSends is not null)
            {
                await betweenSends(cancellationToken);
            }

            first = false;

            var message = ReminderMessage.For(item.Reminder, item.TelegramChatId, item.NoteTitle, _appOptions.PublicBaseUrl);
            if (await _telegramSender.SendAsync(message, cancellationToken))
            {
                item.Reminder.MarkSent(nowUtc);
                _withheld.Forget(item.Reminder.Id.Value);
                delivered++;
            }
            else
            {
                // Left pending on purpose: the next tick retries it.
                _logger.LogWarning(
                    "Reminder {ReminderId} of user {UserId} could not be delivered; it stays pending",
                    item.Reminder.Id.Value, item.Reminder.UserId.Value);
            }
        }

        // One save for the whole tick: a reminder that failed has nothing to save anyway.
        if (delivered > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return delivered;
    }
}
