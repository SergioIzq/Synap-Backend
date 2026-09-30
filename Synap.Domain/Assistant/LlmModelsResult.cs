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

    /// <summary>Groq itself could not be reached or used - the provider, not Synap.</summary>
    Unavailable,

    /// <summary>
    /// Synap's own AI service could not be reached, or refused the call, so Groq was never
    /// contacted. Kept apart from <see cref="Unavailable"/> because blaming the provider for a
    /// failure between Synap's own containers sends whoever reads it looking in the wrong place
    /// (specs/user-settings "AI service unreachable during validation").
    /// </summary>
    ServiceUnavailable,
}
