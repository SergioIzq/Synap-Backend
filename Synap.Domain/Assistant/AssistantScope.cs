namespace Synap.Domain;

/// <summary>
/// What a question is about (scoped-assistant): one of the user's notes, or one of their tags.
/// Exactly one of the two is set - validated by AskAssistantQueryHandler.
/// </summary>
public sealed record AssistantScope(Guid? NoteId, string? Tag);

/// <summary>An earlier question and answer of the same scoped conversation, sent by the client.</summary>
public sealed record AssistantTurn(string Question, string Answer);
