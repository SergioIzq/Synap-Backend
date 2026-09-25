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
    Task GenerateEmbeddingAsync(Guid noteId, Guid userId, string content, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RelatedNote>> GetRelatedNotesAsync(Guid noteId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates with the user's own decrypted Groq key and chosen model (null = the AI
    /// service's default model) - byok-groq-and-settings, there is no server-owned key.
    /// </summary>
    Task<AssistantAnswer> AskAsync(Guid userId, string question, string groqApiKey, string? groqModel, CancellationToken cancellationToken = default);

    /// <summary>Validates a Groq key and lists the chat models it can use, in one call.</summary>
    Task<LlmModelsResult> ListModelsAsync(string groqApiKey, CancellationToken cancellationToken = default);
}
