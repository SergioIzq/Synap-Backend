using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Assistant.Queries;

/// <summary>
/// <paramref name="Scope"/> limits the question to one note or one tag, and <paramref name="History"/>
/// carries the earlier turns of that scoped conversation (scoped-assistant) - both optional:
/// without them the question is about the whole vault, exactly as before.
///
/// <paramref name="Timezone"/> is the browser's IANA timezone (assistant-reminders): the assistant
/// resolves "el viernes" against it, and it is remembered for a snooze pressed later in Telegram.
/// Omitted by older clients, and then moments resolve in UTC.
/// </summary>
public sealed record AskAssistantQuery(
    string Question,
    AssistantScope? Scope = null,
    IReadOnlyList<AssistantTurn>? History = null,
    string? Timezone = null) : IQuery<AssistantAnswer>;
