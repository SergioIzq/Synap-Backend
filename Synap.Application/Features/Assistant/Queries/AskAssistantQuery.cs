using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Assistant.Queries;

/// <summary>
/// <paramref name="Scope"/> limits the question to one note or one tag, and <paramref name="History"/>
/// carries the earlier turns of that scoped conversation (scoped-assistant) - both optional:
/// without them the question is about the whole vault, exactly as before.
/// </summary>
public sealed record AskAssistantQuery(
    string Question,
    AssistantScope? Scope = null,
    IReadOnlyList<AssistantTurn>? History = null) : IQuery<AssistantAnswer>;
