using Microsoft.Extensions.Logging;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Synap.Infrastructure.Services.Ai;

/// <summary>
/// HTTP client for the Python AI service. Every method swallows connectivity failures (the AI
/// service being down entirely, not just its own LLM provider - that graceful case is already
/// handled Python-side) and degrades to an empty/unavailable result instead of throwing, so a
/// blip in the AI service never turns into a raw 500 for the user (specs/ai-assistant).
/// </summary>
public sealed class AiServiceClient : IAiServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AiServiceClient> _logger;

    public AiServiceClient(HttpClient httpClient, ILogger<AiServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task GenerateEmbeddingAsync(Guid noteId, Guid userId, string content, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/internal/embeddings/generate",
                new GenerateEmbeddingRequest(noteId, userId, content),
                cancellationToken);

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not generate embedding for note {NoteId}", noteId);
        }
    }

    public async Task<IReadOnlyList<RelatedNote>> GetRelatedNotesAsync(Guid noteId, Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _httpClient.GetFromJsonAsync<List<RelatedNoteResponse>>(
                $"/internal/notes/{noteId}/related?user_id={userId}",
                cancellationToken);

            return results?.Select(r => new RelatedNote(r.Id, r.Title, r.Content, Enum.Parse<NoteType>(r.Type, ignoreCase: true), r.Similarity)).ToList()
                ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not fetch related notes for note {NoteId}", noteId);
            return [];
        }
    }

    public async Task<AssistantAnswer> AskAsync(Guid userId, string question, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/internal/assistant/ask",
                new AskRequest(userId, question),
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AskResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Empty response from AI service.");

            return new AssistantAnswer(result.Answer, result.SourceNoteIds, result.Grounded);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "AI service unavailable while answering a question for user {UserId}", userId);
            return new AssistantAnswer("The assistant is temporarily unavailable - please try again shortly.", [], false);
        }
    }

    private sealed record GenerateEmbeddingRequest(
        [property: JsonPropertyName("note_id")] Guid NoteId,
        [property: JsonPropertyName("user_id")] Guid UserId,
        [property: JsonPropertyName("content")] string Content);

    private sealed record RelatedNoteResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("similarity")] double Similarity);

    private sealed record AskRequest(
        [property: JsonPropertyName("user_id")] Guid UserId,
        [property: JsonPropertyName("question")] string Question);

    private sealed record AskResponse(
        [property: JsonPropertyName("answer")] string Answer,
        [property: JsonPropertyName("source_note_ids")] List<Guid> SourceNoteIds,
        [property: JsonPropertyName("grounded")] bool Grounded);
}
