using Synap.Domain;

namespace Synap.Application.Features.Settings;

/// <summary>
/// The "briefing" block of the user's settings (specs/briefing "The briefing is off until the user
/// turns it on"). <see cref="CanBeDelivered"/> is what lets the web app warn a user who turned the
/// briefing on without a linked chat, which is the only part of that failure they can fix
/// themselves (daily-briefing design.md Decision 6).
/// </summary>
public sealed record BriefingSettingsResponse(bool Enabled, int? Hour, bool CanBeDelivered)
{
    public static BriefingSettingsResponse From(User user)
        => new(user.BriefingEnabled, user.BriefingHour, user.HasTelegram);
}
