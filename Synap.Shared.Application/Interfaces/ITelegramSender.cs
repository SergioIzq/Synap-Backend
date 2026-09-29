namespace Synap.Shared.Application.Interfaces;

/// <summary>
/// One button under a message. <paramref name="Data"/> is the opaque payload Telegram hands back
/// when the button is pressed (assistant-reminders design.md Decision 4).
/// </summary>
public sealed record TelegramButton(string Label, string Data);

/// <summary>
/// A message to a chat, with the buttons to show under it. An empty <see cref="Buttons"/> - or a
/// sender configured without inline buttons - sends plain text.
/// </summary>
public sealed record TelegramMessage(string ChatId, string Text, IReadOnlyList<TelegramButton> Buttons)
{
    public TelegramMessage(string chatId, string text) : this(chatId, text, [])
    {
    }
}

/// <summary>
/// Sends reminder notifications over the Telegram Bot API (specs/reminders). Unlike
/// <see cref="IEmailSender"/> this is awaited, not queued: the delivery poller has to know whether
/// a reminder went out before it marks it as delivered, and a failure must leave it pending.
/// </summary>
public interface ITelegramSender
{
    /// <summary>
    /// True when Telegram delivery is configured and turned on. False means every call here is a
    /// no-op that reports failure without making a request (specs/reminders "Reminders are inert
    /// while delivery is turned off").
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Sends the message. False - never an exception - for any failure, including an invalid chat,
    /// a blocked bot or an unreachable Telegram: the caller leaves the reminder pending and retries.
    /// </summary>
    Task<bool> SendAsync(TelegramMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a sent message's text and drops its buttons - how a confirmed, postponed or
    /// cancelled reminder stops offering the options it was delivered with. False on any failure.
    /// </summary>
    Task<bool> EditAsync(string chatId, long messageId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges a button press so Telegram stops showing its loading state. Best effort: a
    /// failure here never changes what happened to the reminder.
    /// </summary>
    Task AnswerCallbackAsync(string callbackId, string? toast = null, CancellationToken cancellationToken = default);
}
