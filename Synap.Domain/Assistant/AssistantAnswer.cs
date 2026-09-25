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

    public static AssistantAnswer Failed(string message, AssistantAnswerStatus status) => new(message, [], false, status);
}

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
}
