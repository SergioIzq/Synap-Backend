using MediatR;
using Microsoft.Extensions.Logging;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Briefing;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Reminders.Commands;

/// <summary>
/// One update from the Telegram webhook (assistant-reminders design.md Decision 4): either a
/// message the user typed - only `/start &lt;code&gt;` means anything to us - or a press on one of
/// a delivered reminder's buttons.
///
/// Runs unauthenticated: the chat id, and the link code before it, are the only things that tie an
/// update to an account. Every path here is silent about users it doesn't act on.
/// </summary>
public sealed record ProcessTelegramUpdateCommand(TelegramUpdate Update) : ICommand;

/// <summary>Only the fields Synap uses, mapped from Telegram's much larger update object.</summary>
public sealed record TelegramUpdate(TelegramIncomingMessage? Message, TelegramCallback? Callback);

public sealed record TelegramIncomingMessage(string ChatId, string? Text);

public sealed record TelegramCallback(string CallbackId, string ChatId, long MessageId, string? Data);

public sealed class ProcessTelegramUpdateCommandHandler : ICommandHandler<ProcessTelegramUpdateCommand>
{
    public const string LinkedMessage = "¡Listo! Recibirás tus recordatorios aquí 🔔";
    public const string LinkFailedMessage = "Ese código no vale o ha caducado. Genera uno nuevo en Configuración y vuelve a enviármelo.";
    public const string UnknownCommandMessage = "Soy el bot de Synap. Para recibir tus recordatorios aquí, ve a Configuración en Synap y envíame el código que te dé.";
    public const string BriefingFailedMessage = "No he podido prepararte el briefing ahora mismo. Inténtalo de nuevo en un momento.";

    /// <summary>The briefing on demand; the only command besides /start that the bot acts on.</summary>
    public const string BriefingCommand = "/briefing";

    private static readonly TimeSpan SnoozeHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan MorningHour = TimeSpan.FromHours(9);

    private readonly ISender _sender;
    private readonly IReminderWriteRepository _reminderWriteRepository;
    private readonly IUserReadRepository _userReadRepository;
    private readonly BriefingDispatcher _briefingDispatcher;
    private readonly ITelegramSender _telegramSender;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ProcessTelegramUpdateCommandHandler> _logger;

    public ProcessTelegramUpdateCommandHandler(
        ISender sender,
        IReminderWriteRepository reminderWriteRepository,
        IUserReadRepository userReadRepository,
        BriefingDispatcher briefingDispatcher,
        ITelegramSender telegramSender,
        IUnitOfWork unitOfWork,
        ILogger<ProcessTelegramUpdateCommandHandler> logger)
    {
        _sender = sender;
        _reminderWriteRepository = reminderWriteRepository;
        _userReadRepository = userReadRepository;
        _briefingDispatcher = briefingDispatcher;
        _telegramSender = telegramSender;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result> Handle(ProcessTelegramUpdateCommand request, CancellationToken cancellationToken)
    {
        if (request.Update.Callback is { } callback)
        {
            await HandleCallbackAsync(callback, cancellationToken);
        }
        else if (request.Update.Message is { } message)
        {
            await HandleMessageAsync(message, cancellationToken);
        }

        // Always success: the webhook answers 200 whatever the update was, so Telegram doesn't retry.
        return Result.Success();
    }

    // ---- /start <code> (task 6.2) ----

    private async Task HandleMessageAsync(TelegramIncomingMessage message, CancellationToken cancellationToken)
    {
        var text = message.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            await ReplyAsync(message.ChatId, UnknownCommandMessage, cancellationToken);
            return;
        }

        if (text.StartsWith(BriefingCommand, StringComparison.OrdinalIgnoreCase))
        {
            await HandleBriefingAsync(message.ChatId, cancellationToken);
            return;
        }

        if (!text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(message.ChatId, UnknownCommandMessage, cancellationToken);
            return;
        }

        var code = text["/start".Length..].Trim();
        var linked = await _sender.Send(new CompleteTelegramLinkCommand(code, message.ChatId), cancellationToken);

        // The same message whether the code was unknown, expired or already used: nothing about any
        // account leaks to whoever sent it (specs/reminders "Unknown code").
        await ReplyAsync(message.ChatId, linked is { IsSuccess: true, Value: true } ? LinkedMessage : LinkFailedMessage, cancellationToken);
    }

    // ---- /briefing (daily-briefing task 5.4) ----

    /// <summary>
    /// The briefing on demand, for whoever this chat belongs to (specs/briefing "Asked for from
    /// the bot"). A chat linked to no account gets the reply anything unrecognised gets: it learns
    /// nothing about whether an account exists, the same silence a bad /start code gets.
    /// </summary>
    private async Task HandleBriefingAsync(string chatId, CancellationToken cancellationToken)
    {
        var owner = await _userReadRepository.GetByTelegramChatIdAsync(chatId, cancellationToken);
        if (owner is null)
        {
            await ReplyAsync(chatId, UnknownCommandMessage, cancellationToken);
            return;
        }

        // The dispatcher sends to the user's own chat, which is this one. Asking does not consume
        // the day, so the automatic briefing still arrives at its hour.
        var result = await _briefingDispatcher.SendAsync(owner, DateTime.UtcNow, answerWhenEmpty: true, cancellationToken);
        if (result != BriefingDispatchResult.Sent)
        {
            _logger.LogWarning(
                "A /briefing asked for by user {UserId} ended as {Result}", owner.Id.Value, result);
            await ReplyAsync(chatId, BriefingFailedMessage, cancellationToken);
        }
    }

    // ---- Button presses (tasks 6.3 and 6.4) ----

    private async Task HandleCallbackAsync(TelegramCallback callback, CancellationToken cancellationToken)
    {
        var parsed = ReminderMessage.Parse(callback.Data);
        if (parsed is null)
        {
            await _telegramSender.AnswerCallbackAsync(callback.CallbackId, cancellationToken: cancellationToken);
            return;
        }

        var owner = await _userReadRepository.GetByTelegramChatIdAsync(callback.ChatId, cancellationToken);
        if (owner is null)
        {
            // A chat nobody has linked - including one that was linked and has since disconnected
            // (specs/reminders "Old buttons are inert").
            await _telegramSender.AnswerCallbackAsync(callback.CallbackId, cancellationToken: cancellationToken);
            return;
        }

        var reminder = await _reminderWriteRepository.GetOwnedByUserAsync(parsed.ReminderId, owner.Id.Value, cancellationToken);
        if (reminder is null)
        {
            await _telegramSender.AnswerCallbackAsync(callback.CallbackId, cancellationToken: cancellationToken);
            return;
        }

        // The recycled row's guard (design.md Decision 2): pressing a button on an older message -
        // or twice on the same one - must not act on the occurrence that has moved on since.
        if (reminder.DueAt != parsed.OccurrenceDueAt)
        {
            await _telegramSender.AnswerCallbackAsync(callback.CallbackId, cancellationToken: cancellationToken);
            return;
        }

        Apply(reminder, parsed.Action, owner.Timezone);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var outcome = ReminderMessage.Outcome(reminder, parsed.Action, owner.Timezone);
        await _telegramSender.AnswerCallbackAsync(callback.CallbackId, cancellationToken: cancellationToken);
        if (!await _telegramSender.EditAsync(callback.ChatId, callback.MessageId, outcome, cancellationToken))
        {
            // The reminder is already updated; only the message the user is looking at is stale.
            _logger.LogWarning("Reminder {ReminderId} was updated but its message could not be edited", reminder.Id.Value);
        }
    }

    private static void Apply(Reminder reminder, ReminderAction action, string? timezone)
    {
        var now = DateTime.UtcNow;

        switch (action)
        {
            case ReminderAction.Confirm:
                reminder.Confirm(now, timezone);
                break;
            case ReminderAction.SnoozeHour:
                reminder.Snooze(now.Add(SnoozeHour));
                break;
            case ReminderAction.SnoozeTomorrow:
                // 09:00 tomorrow in the user's own timezone, not 24 hours from now.
                reminder.Snooze(UserClock.AtLocalTimeOfDay(now, timezone, 1, MorningHour));
                break;
            case ReminderAction.SnoozeNextWeek:
                reminder.Snooze(UserClock.AtLocalTimeOfDay(now, timezone, 7, MorningHour));
                break;
            case ReminderAction.CancelSeries:
                reminder.CancelSeries();
                break;
        }
    }

    private async Task ReplyAsync(string chatId, string text, CancellationToken cancellationToken)
        => await _telegramSender.SendAsync(new TelegramMessage(chatId, text), cancellationToken);
}
