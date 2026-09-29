using Microsoft.Extensions.Logging;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using System.Net.Http.Json;
using System.Text.Json;
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

    public async Task GenerateEmbeddingAsync(Guid noteId, Guid userId, string? title, string content, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/internal/embeddings/generate",
                new GenerateEmbeddingRequest(noteId, userId, title, content),
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

    public async Task<AssistantAnswer> AskAsync(
        Guid userId,
        string question,
        string groqApiKey,
        string? groqModel,
        AssistantScope? scope = null,
        IReadOnlyList<AssistantTurn>? history = null,
        IReadOnlyList<string>? memory = null,
        ActionsUnavailableReason? actionsUnavailable = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/internal/assistant/ask",
                new AskRequest(
                    userId,
                    question,
                    groqApiKey,
                    groqModel,
                    scope?.NoteId,
                    scope?.Tag,
                    history?.Select(t => new AskTurnRequest(t.Question, t.Answer)).ToList() ?? [],
                    memory?.ToList() ?? [],
                    actionsUnavailable switch
                    {
                        ActionsUnavailableReason.Scope => "scope",
                        ActionsUnavailableReason.Model => "model",
                        _ => null,
                    }),
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AskResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Empty response from AI service.");

            return new AssistantAnswer(result.Answer, result.SourceNoteIds, result.Grounded, ParseAnswerStatus(result.Status))
            {
                Sources = result.Sources?.Select(s => new AssistantSource(s.Id, s.Title)).ToList() ?? [],
                PartialContext = result.PartialContext,
                Scope = result.Scope is null ? null : new AssistantScope(result.Scope.NoteId, result.Scope.Tag),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            // Only the user ID is logged - never the request body, which carries the user's key.
            _logger.LogWarning(ex, "AI service unavailable while answering a question for user {UserId}", userId);
            return AssistantAnswer.Failed(AssistantAnswer.UnavailableMessage, AssistantAnswerStatus.Unavailable);
        }
    }

    public async Task<LlmModelsResult> ListModelsAsync(string groqApiKey, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/internal/llm/models",
                new ListModelsRequest(groqApiKey),
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ListModelsResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Empty response from AI service.");

            var status = result.Status switch
            {
                "ok" => LlmKeyStatus.Ok,
                "invalid_key" => LlmKeyStatus.InvalidKey,
                "rate_limited" => LlmKeyStatus.RateLimited,
                _ => LlmKeyStatus.Unavailable,
            };

            return new LlmModelsResult(
                status,
                status == LlmKeyStatus.Ok ? result.Models.Select(m => new LlmModel(m.Id, m.SupportsActions)).ToList() : []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            _logger.LogWarning(ex, "AI service unavailable while listing LLM models");
            return LlmModelsResult.Failed(LlmKeyStatus.Unavailable);
        }
    }

    public async Task<AgentStepResult> StepAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentTool>? tools,
        string groqApiKey,
        string? groqModel,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new StepRequest(
                messages.Select(m => new StepMessage(
                    m.Role,
                    m.Content,
                    m.ToolCalls?.Select(c => new StepToolCall(c.Id, c.Name, c.Arguments, c.ArgumentsError)).ToList(),
                    m.ToolCallId)).ToList(),
                tools?.Select(t => new StepTool(t.Name, t.Description, t.Parameters)).ToList(),
                groqApiKey,
                groqModel);

            using var response = await _httpClient.PostAsJsonAsync("/internal/llm/step", request, StepJsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<StepResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Empty response from AI service.");

            var status = result.Status switch
            {
                "ok" => AgentStepStatus.Ok,
                "invalid_key" => AgentStepStatus.InvalidKey,
                "rate_limited" => AgentStepStatus.RateLimited,
                "tools_unsupported" => AgentStepStatus.ToolsUnsupported,
                "tool_call_failed" => AgentStepStatus.ToolCallFailed,
                _ => AgentStepStatus.Unavailable,
            };

            return status != AgentStepStatus.Ok
                ? AgentStepResult.Failed(status)
                : new AgentStepResult(
                    status,
                    result.Text,
                    result.ToolCalls?.Select(c => new AgentToolCall(c.Id, c.Name, c.Arguments, c.ArgumentsError)).ToList() ?? []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            // Never the request body: it carries the user's key and their notes.
            _logger.LogWarning(ex, "AI service unavailable during an assistant step");
            return AgentStepResult.Failed(AgentStepStatus.Unavailable);
        }
    }

    public async Task<IReadOnlyList<NoteSearchHit>> SearchAsync(Guid userId, string query, int limit, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/internal/search", new SearchRequest(userId, query, limit), cancellationToken);
            response.EnsureSuccessStatusCode();

            var results = await response.Content.ReadFromJsonAsync<List<SearchResultResponse>>(cancellationToken) ?? [];
            return results.Select(r => new NoteSearchHit(r.Id, r.Title, r.Type, r.Tags, r.Snippet)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            _logger.LogWarning(ex, "AI service unavailable while searching notes for user {UserId}", userId);
            return [];
        }
    }

    // Python sends snake_case statuses; anything unrecognised is treated as unavailable rather
    // than trusted as a real answer.
    private static AssistantAnswerStatus ParseAnswerStatus(string? status) => status switch
    {
        "ok" => AssistantAnswerStatus.Ok,
        "no_relevant_notes" => AssistantAnswerStatus.NoRelevantNotes,
        "invalid_key" => AssistantAnswerStatus.InvalidKey,
        "rate_limited" => AssistantAnswerStatus.RateLimited,
        "scope_unsupported" => AssistantAnswerStatus.ScopeUnsupported,
        _ => AssistantAnswerStatus.Unavailable,
    };

    private sealed record GenerateEmbeddingRequest(
        [property: JsonPropertyName("note_id")] Guid NoteId,
        [property: JsonPropertyName("user_id")] Guid UserId,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("content")] string Content);

    private sealed record RelatedNoteResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("similarity")] double Similarity);

    private sealed record AskRequest(
        [property: JsonPropertyName("user_id")] Guid UserId,
        [property: JsonPropertyName("question")] string Question,
        [property: JsonPropertyName("groq_api_key")] string GroqApiKey,
        [property: JsonPropertyName("groq_model")] string? GroqModel,
        [property: JsonPropertyName("scope_note_id")] Guid? ScopeNoteId,
        [property: JsonPropertyName("scope_tag")] string? ScopeTag,
        [property: JsonPropertyName("history")] List<AskTurnRequest> History,
        [property: JsonPropertyName("memory")] List<string> Memory,
        [property: JsonPropertyName("actions_unavailable")] string? ActionsUnavailable);

    private sealed record AskTurnRequest(
        [property: JsonPropertyName("question")] string Question,
        [property: JsonPropertyName("answer")] string Answer);

    private sealed record AskResponse(
        [property: JsonPropertyName("answer")] string Answer,
        [property: JsonPropertyName("source_note_ids")] List<Guid> SourceNoteIds,
        [property: JsonPropertyName("grounded")] bool Grounded,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("sources")] List<AskSourceResponse>? Sources,
        [property: JsonPropertyName("partial_context")] bool? PartialContext = null,
        [property: JsonPropertyName("scope")] AskScopeResponse? Scope = null);

    private sealed record AskScopeResponse(
        [property: JsonPropertyName("note_id")] Guid? NoteId,
        [property: JsonPropertyName("tag")] string? Tag);

    private sealed record AskSourceResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("title")] string Title);

    private sealed record ListModelsRequest(
        [property: JsonPropertyName("api_key")] string ApiKey);

    private sealed record ListModelsResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("models")] List<ModelResponse> Models);

    private sealed record ModelResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("supports_actions")] bool SupportsActions);

    // Null tool fields are left out: the AI service's schema treats a missing field and null alike,
    // but a tool message with "tool_calls": null is noise in every request.
    private static readonly JsonSerializerOptions StepJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record StepRequest(
        [property: JsonPropertyName("messages")] List<StepMessage> Messages,
        [property: JsonPropertyName("tools")] List<StepTool>? Tools,
        [property: JsonPropertyName("groq_api_key")] string GroqApiKey,
        [property: JsonPropertyName("groq_model")] string? GroqModel);

    private sealed record StepMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("tool_calls")] List<StepToolCall>? ToolCalls,
        [property: JsonPropertyName("tool_call_id")] string? ToolCallId);

    private sealed record StepToolCall(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] JsonElement? Arguments,
        [property: JsonPropertyName("arguments_error")] string? ArgumentsError);

    private sealed record StepTool(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters);

    private sealed record StepResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("tool_calls")] List<StepToolCall>? ToolCalls);

    private sealed record SearchRequest(
        [property: JsonPropertyName("user_id")] Guid UserId,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("limit")] int Limit);

    private sealed record SearchResultResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("tags")] List<string> Tags,
        [property: JsonPropertyName("snippet")] string Snippet);
}
