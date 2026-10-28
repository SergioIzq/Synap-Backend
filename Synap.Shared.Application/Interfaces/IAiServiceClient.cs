using Synap.Domain;

namespace Synap.Shared.Application.Interfaces;

/// <summary>
/// Talks to the Python AI service over HTTP (design.md Decision 1: it owns the embeddings and
/// the pgvector queries, the .NET API owns the relational schema). Every method degrades
/// gracefully instead of throwing when the AI service itself is unreachable - see
/// AiServiceClient's own comment and specs/ai-assistant.
/// </summary>
public interface IAiServiceClient
{
    Task GenerateEmbeddingAsync(Guid noteId, Guid userId, string? title, string content, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RelatedNote>> GetRelatedNotesAsync(Guid noteId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates with the user's own decrypted Groq key and chosen model (null = the AI
    /// service's default model) - byok-groq-and-settings, there is no server-owned key.
    /// <paramref name="scope"/> limits the answer to one note or tag and <paramref name="history"/>
    /// carries the earlier turns of the conversation (scoped-assistant, and global ones since
    /// assistant-agent-foundations) - both already validated, ownership-checked and trimmed by the
    /// caller. <paramref name="memory"/> is the user's memory entries (specs/assistant-memory), and
    /// <paramref name="actionsUnavailable"/> says why this answer comes without actions.
    /// </summary>
    Task<AssistantAnswer> AskAsync(
        Guid userId,
        string question,
        string groqApiKey,
        string? groqModel,
        AssistantScope? scope = null,
        IReadOnlyList<AssistantTurn>? history = null,
        IReadOnlyList<string>? memory = null,
        ActionsUnavailableReason? actionsUnavailable = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One generation step of the assistant's tool loop (assistant-agent-foundations design.md
    /// Decision 1), with the user's own key. Never throws: an unreachable AI service is
    /// <see cref="AgentStepStatus.Unavailable"/>.
    /// </summary>
    Task<AgentStepResult> StepAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentTool>? tools,
        string groqApiKey,
        string? groqModel,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hybrid search over the user's own notes; empty when the AI service is unreachable.
    /// <paramref name="minSimilarity"/> filters semantic hits below the threshold; words-only hits
    /// (similarity=null) are always kept (calendar-integration design.md Decision 8).
    /// </summary>
    Task<IReadOnlyList<NoteSearchHit>> SearchAsync(Guid userId, string query, int limit, double? minSimilarity = null, CancellationToken cancellationToken = default);

    /// <summary>Validates a Groq key and lists the chat models it can use, in one call.</summary>
    Task<LlmModelsResult> ListModelsAsync(string groqApiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Conexiones de una nota filtradas por umbral (smart-resurfacing, Decisión 1).
    /// Devuelve <see cref="NoteConnectionStatus.Pending"/> cuando la nota aún no tiene embedding
    /// del modelo actual. Lanza <see cref="HttpRequestException"/> si el ai-service no responde
    /// (el caller decide si propagar o traducir a 503).
    /// </summary>
    Task<NoteConnectionsResult> GetConnectionsAsync(
        Guid noteId,
        Guid userId,
        double minSimilarity,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pares (candidata, motivo) para el rescate del briefing (smart-resurfacing, Decisión 5).
    /// Devuelve vacío cuando alguna lista está vacía o el ai-service no responde.
    /// </summary>
    Task<IReadOnlyList<SimilarityPair>> GetSimilarityPairsAsync(
        Guid userId,
        IReadOnlyList<Guid> anchorIds,
        IReadOnlyList<Guid> candidateIds,
        double minSimilarity,
        int limit,
        CancellationToken cancellationToken = default);
}
