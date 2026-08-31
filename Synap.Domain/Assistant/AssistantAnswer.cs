namespace Synap.Domain;

/// <summary>
/// Always a "successful" shape from the caller's perspective - a failed retrieval or an
/// unavailable LLM provider is represented as `Grounded = false` with a clear message, not an
/// exception (specs/ai-assistant "graceful handling of generation provider failure").
/// </summary>
public sealed record AssistantAnswer(string Answer, IReadOnlyList<Guid> SourceNoteIds, bool Grounded);
