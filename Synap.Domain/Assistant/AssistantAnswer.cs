using System.Text.Json.Serialization;

namespace Synap.Domain;

/// <summary>
/// Always a "successful" shape from the caller's perspective - a failed retrieval or an
/// unavailable LLM provider is represented as `Grounded = false` with a clear message and a
/// typed <see cref="Status"/>, not an exception (specs/ai-assistant "graceful handling of
/// generation provider failure").
/// </summary>
public sealed record AssistantAnswer(string Answer, IReadOnlyList<Guid> SourceNoteIds, bool Grounded, AssistantAnswerStatus Status)
{
    public const string KeyMissingMessage = "Necesitas configurar tu API key de Groq en Configuración para usar el asistente.";
    public const string InvalidKeyMessage = "Tu API key de Groq ya no es válida. Actualízala en Configuración.";
    public const string UnavailableMessage = "El asistente no está disponible temporalmente. Inténtalo de nuevo en un momento.";
    public const string RateLimitedMessage = "Has alcanzado el límite de tu cuota de Groq. Inténtalo de nuevo más tarde.";
    public const string ScopeUnsupportedMessage =
        "Todavía no puedo responder sobre enlaces: solo guardo la dirección, no el contenido del artículo.";

    /// <summary>
    /// The notes the answer was grounded in, with a displayable title - the web app links to
    /// them (specs/ai-assistant "Navigable answer sources"). SourceNoteIds is kept for clients
    /// deployed before sources existed.
    /// </summary>
    public IReadOnlyList<AssistantSource> Sources { get; init; } = [];

    /// <summary>Scoped answers only: true when the notes were cut down to fit the context budget.</summary>
    public bool? PartialContext { get; init; }

    /// <summary>Scoped answers only: the note or tag the answer is about.</summary>
    public AssistantScope? Scope { get; init; }

    /// <summary>
    /// What the assistant did while answering, in order (specs/ai-assistant "Actions shown in the
    /// answer") - always present, empty when it only answered.
    /// </summary>
    public IReadOnlyList<AssistantAction> Actions { get; init; } = [];

    public static AssistantAnswer Failed(string message, AssistantAnswerStatus status) => new(message, [], false, status);
}

/// <summary>Title is the note's own title, or a content preview when it has none.</summary>
public sealed record AssistantSource(Guid Id, string Title);

/// <summary>
/// byok-groq-and-settings design.md Decision 4 - one status per outcome, so the web app can
/// map each to its own message and action (e.g. a link to Settings for KeyMissing/InvalidKey).
/// Wire names are pinned here because the kernel's result handler serializes enums with its own
/// options (PascalCase), ignoring the camelCase converter configured in Program.cs.
/// </summary>
public enum AssistantAnswerStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("noRelevantNotes")] NoRelevantNotes,
    [JsonStringEnumMemberName("keyMissing")] KeyMissing,
    [JsonStringEnumMemberName("invalidKey")] InvalidKey,
    [JsonStringEnumMemberName("rateLimited")] RateLimited,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
    /// <summary>The scope can't be answered yet - a bookmark only holds its link (scoped-assistant).</summary>
    [JsonStringEnumMemberName("scopeUnsupported")] ScopeUnsupported,
}

/// <summary>
/// One action the assistant performed (assistant-agent-foundations design.md Decision 2):
/// <see cref="NoteId"/>, <see cref="Title"/> and <see cref="Tags"/> for note actions,
/// <see cref="Text"/> for a saved memory entry.
/// </summary>
public sealed record AssistantAction(
    AssistantActionType Type,
    Guid? NoteId = null,
    string? Title = null,
    IReadOnlyList<string>? Tags = null,
    string? Text = null);

public enum AssistantActionType
{
    [JsonStringEnumMemberName("noteCreated")] NoteCreated,
    [JsonStringEnumMemberName("tagsAdded")] TagsAdded,
    [JsonStringEnumMemberName("memorySaved")] MemorySaved,
}
