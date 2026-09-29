namespace Synap.Domain;

/// <summary>
/// Result of asking the provider which models a given API key can use - which doubles as
/// validating that key (byok-groq-and-settings design.md Decision 3).
/// </summary>
public sealed record LlmModelsResult(LlmKeyStatus Status, IReadOnlyList<LlmModel> Models)
{
    public static LlmModelsResult Failed(LlmKeyStatus status) => new(status, []);
}

/// <summary>A chat model; <see cref="SupportsActions"/> says whether the assistant can use tools with it (assistant-agent-foundations).</summary>
public sealed record LlmModel(string Id, bool SupportsActions);

public enum LlmKeyStatus
{
    Ok,
    InvalidKey,
    RateLimited,
    Unavailable,
}
