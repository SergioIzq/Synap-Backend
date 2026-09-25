using Synap.Domain;

namespace Synap.Application.Features.Settings;

/// <summary>
/// The "ai" block of the user's settings. Never carries the key itself - only whether one is
/// configured and a masked form built from its last four characters (specs/user-settings
/// "Stored key is never disclosed").
/// </summary>
public sealed record AiSettingsResponse(
    bool HasGroqKey,
    string? GroqKeyMasked,
    DateTime? GroqKeyUpdatedAt,
    string? GroqModel,
    string DefaultGroqModel)
{
    public static AiSettingsResponse From(User user, string defaultGroqModel) => new(
        user.HasGroqApiKey,
        user.HasGroqApiKey ? $"gsk_…{user.GroqApiKeyLast4}" : null,
        user.GroqApiKeyUpdatedAt,
        user.GroqModel,
        defaultGroqModel);
}

public sealed record SettingsResponse(string Email, AiSettingsResponse Ai);
