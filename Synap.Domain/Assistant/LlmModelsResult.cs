namespace Synap.Domain;

/// <summary>
/// Result of asking the provider which models a given API key can use - which doubles as
/// validating that key (byok-groq-and-settings design.md Decision 3).
/// </summary>
public sealed record LlmModelsResult(LlmKeyStatus Status, IReadOnlyList<string> Models)
{
    public static LlmModelsResult Failed(LlmKeyStatus status) => new(status, []);
}

public enum LlmKeyStatus
{
    Ok,
    InvalidKey,
    RateLimited,
    Unavailable,
}
