namespace Synap.Application.Features.Reminders;

/// <summary>
/// Bound from "Telegram" (assistant-reminders design.md Migration Plan). Lives here rather than in
/// Infrastructure, like <see cref="Users.AppOptions"/>: both the link command and the sender need
/// it, and Infrastructure references Application, not the other way round.
///
/// Off by default: the API starts and reminders can be managed with no bot configured at all,
/// nothing is just delivered. BotToken and WebhookSecret are never committed - user-secrets
/// locally, TELEGRAM_BOT_TOKEN / TELEGRAM_WEBHOOK_SECRET in the .env on Docker/VPS.
/// </summary>
public sealed class TelegramSettings
{
    public const string SectionName = "Telegram";

    /// <summary>The kill switch of the Migration Plan: deploy with it false, turn it on afterwards.</summary>
    public bool Enabled { get; set; }

    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Sent by Telegram as the X-Telegram-Bot-Api-Secret-Token header on every webhook call, so the
    /// endpoint can reject anything that didn't come from Telegram (design.md Decision 4).
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Shown in Settings as "envía /start &lt;código&gt; a @…"; no leading "@".</summary>
    public string BotUsername { get; set; } = string.Empty;

    /// <summary>
    /// Inline buttons need a reachable webhook to answer callbacks. Without one - typically local
    /// development without a tunnel - reminders are still sent, just without buttons
    /// (design.md Risks).
    /// </summary>
    public bool InlineButtons { get; set; } = true;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BotToken);
}
