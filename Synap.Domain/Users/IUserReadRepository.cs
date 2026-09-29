using Synap.Shared.Domain.ValueObjects;

namespace Synap.Domain;

public interface IUserReadRepository
{
    Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken = default);
    Task<User?> GetByApiTokenHashAsync(string apiTokenHash, CancellationToken cancellationToken = default);
    /// <summary>Null when the user doesn't exist.</summary>
    Task<string?> GetSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<User?> GetByPasswordResetTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user holding this Telegram link code, or null. The code is stored as-is (unlike the
    /// password-reset token's hash): it only ever links a chat, never authenticates
    /// (assistant-reminders design.md Decision 5).
    /// </summary>
    Task<User?> GetByTelegramLinkTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user who has this Telegram chat linked, or null - which is also the answer for a chat
    /// that was linked and has since been disconnected (specs/reminders "Old buttons are inert").
    /// </summary>
    Task<User?> GetByTelegramChatIdAsync(string chatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the user has a Telegram chat linked, and the timezone their reminders resolve in
    /// (assistant-reminders). Null when the user doesn't exist; only these two fields, so the
    /// callers that just need the connection state don't load the whole aggregate.
    /// </summary>
    Task<UserReminderSettings?> GetReminderSettingsAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record UserReminderSettings(string? TelegramChatId, string? Timezone)
{
    public bool TelegramConnected => TelegramChatId is not null;
}
