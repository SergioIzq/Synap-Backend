using Microsoft.Extensions.Logging.Abstractions;
using Synap.Domain;
using Synap.Infrastructure.Services.Ai;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Synap.UnitTests.Services.Ai;

/// <summary>
/// Runnable without any external dependency (no Docker/Postgres/Python needed) - covers task
/// 5.2's .NET-side contract: the client must forward the *correct* user's ID, and must never
/// let an unreachable AI service surface as an unhandled exception (specs/ai-assistant).
/// </summary>
public class AiServiceClientTests
{
    private static AiServiceClient CreateClient(FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://ai-service.test") };
        return new AiServiceClient(httpClient, NullLogger<AiServiceClient>.Instance);
    }

    [Fact]
    public async Task AskAsync_sends_the_requesting_users_id_not_a_stale_or_default_one()
    {
        var userId = Guid.NewGuid();
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            """{"answer": "You fixed it by clearing the cache.", "source_note_ids": [], "grounded": true, "status": "ok"}""");

        var client = CreateClient(handler);

        await client.AskAsync(userId, "How did I fix the build last time?", "gsk_user_key", null);

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains(userId.ToString(), handler.LastRequestBody);
    }

    [Fact]
    public async Task AskAsync_maps_a_grounded_response_correctly()
    {
        var noteId = Guid.NewGuid();
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            $$"""{"answer": "Answer text", "source_note_ids": ["{{noteId}}"], "grounded": true, "status": "ok"}""");

        var answer = await CreateClient(handler).AskAsync(Guid.NewGuid(), "question", "gsk_user_key", null);

        Assert.True(answer.Grounded);
        Assert.Equal("Answer text", answer.Answer);
        Assert.Equal([noteId], answer.SourceNoteIds);
        Assert.Equal(AssistantAnswerStatus.Ok, answer.Status);
    }

    [Fact]
    public async Task AskAsync_maps_sources_with_titles()
    {
        var noteId = Guid.NewGuid();
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            $$"""{"answer": "a", "source_note_ids": ["{{noteId}}"], "sources": [{"id": "{{noteId}}", "title": "Fix CORS"}], "grounded": true, "status": "ok"}""");

        var answer = await CreateClient(handler).AskAsync(Guid.NewGuid(), "question", "gsk_user_key", null);

        Assert.Equal([new AssistantSource(noteId, "Fix CORS")], answer.Sources);
        Assert.Equal([noteId], answer.SourceNoteIds);
    }

    [Fact]
    public async Task AskAsync_tolerates_an_ai_service_without_sources()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            """{"answer": "a", "source_note_ids": [], "grounded": false, "status": "no_relevant_notes"}""");

        var answer = await CreateClient(handler).AskAsync(Guid.NewGuid(), "question", "gsk_user_key", null);

        Assert.Empty(answer.Sources);
    }

    [Fact]
    public async Task AskAsync_forwards_the_users_own_key_and_model()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            """{"answer": "a", "source_note_ids": [], "grounded": false, "status": "no_relevant_notes"}""");

        var answer = await CreateClient(handler).AskAsync(Guid.NewGuid(), "question", "gsk_user_key", "llama-3.3-70b");

        Assert.Contains("\"groq_api_key\":\"gsk_user_key\"", handler.LastRequestBody);
        Assert.Contains("\"groq_model\":\"llama-3.3-70b\"", handler.LastRequestBody);
        Assert.Equal(AssistantAnswerStatus.NoRelevantNotes, answer.Status);
    }

    [Theory]
    [InlineData("invalid_key", AssistantAnswerStatus.InvalidKey)]
    [InlineData("rate_limited", AssistantAnswerStatus.RateLimited)]
    [InlineData("unavailable", AssistantAnswerStatus.Unavailable)]
    [InlineData("something_new", AssistantAnswerStatus.Unavailable)]
    public async Task AskAsync_maps_every_status(string wireStatus, AssistantAnswerStatus expected)
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            $$"""{"answer": "m", "source_note_ids": [], "grounded": false, "status": "{{wireStatus}}"}""");

        var answer = await CreateClient(handler).AskAsync(Guid.NewGuid(), "question", "gsk_user_key", null);

        Assert.Equal(expected, answer.Status);
    }

    [Fact]
    public async Task ListModelsAsync_maps_an_ok_response()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            """{"status": "ok", "models": [{"id": "a", "supports_actions": true}, {"id": "b", "supports_actions": false}]}""");

        var result = await CreateClient(handler).ListModelsAsync("gsk_user_key");

        Assert.Equal(LlmKeyStatus.Ok, result.Status);
        Assert.Equal([new LlmModel("a", true), new LlmModel("b", false)], result.Models);
        Assert.Contains("\"api_key\":\"gsk_user_key\"", handler.LastRequestBody);
        Assert.Equal("/internal/llm/models", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("invalid_key", LlmKeyStatus.InvalidKey)]
    [InlineData("rate_limited", LlmKeyStatus.RateLimited)]
    [InlineData("unavailable", LlmKeyStatus.Unavailable)]
    public async Task ListModelsAsync_maps_failures(string wireStatus, LlmKeyStatus expected)
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, $$"""{"status": "{{wireStatus}}", "models": []}""");

        var result = await CreateClient(handler).ListModelsAsync("gsk_user_key");

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task ListModelsAsync_degrades_to_unavailable_when_unreachable()
    {
        var handler = FakeHttpMessageHandler.Throwing(new HttpRequestException("Connection refused"));

        var result = await CreateClient(handler).ListModelsAsync("gsk_user_key");

        Assert.Equal(LlmKeyStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task AskAsync_degrades_gracefully_when_the_ai_service_is_unreachable()
    {
        var handler = FakeHttpMessageHandler.Throwing(new HttpRequestException("Connection refused"));

        var answer = await CreateClient(handler).AskAsync(Guid.NewGuid(), "question", "gsk_user_key", null);

        // Never an exception bubbling up, and never a "grounded" (i.e. trustworthy) answer -
        // specs/ai-assistant "graceful handling of generation provider failure" applies just as
        // much when the AI service itself is down, not only its own LLM provider.
        Assert.False(answer.Grounded);
        Assert.Empty(answer.SourceNoteIds);
        Assert.NotEmpty(answer.Answer);
        Assert.Equal(AssistantAnswerStatus.Unavailable, answer.Status);
    }

    [Fact]
    public async Task GetRelatedNotesAsync_degrades_to_an_empty_list_when_unreachable()
    {
        var handler = FakeHttpMessageHandler.Throwing(new TaskCanceledException("Timed out"));

        var related = await CreateClient(handler).GetRelatedNotesAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Empty(related);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_does_not_throw_when_the_ai_service_is_unreachable()
    {
        var handler = FakeHttpMessageHandler.Throwing(new HttpRequestException("Connection refused"));

        // The embedding job runs in the background (design.md Decision 8) - a throw here would
        // just be logged and swallowed by QueuedJobHostedService anyway, but the client itself
        // should already not throw for this specific, expected failure mode.
        await CreateClient(handler).GenerateEmbeddingAsync(Guid.NewGuid(), Guid.NewGuid(), "title", "content");
    }

    [Fact]
    public async Task AskAsync_sends_the_users_memory()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{"answer": "a", "source_note_ids": [], "grounded": true, "status": "ok"}""");

        await CreateClient(handler).AskAsync(Guid.NewGuid(), "q", "gsk_user_key", null, memory: ["prefiero respuestas cortas"]);

        Assert.Contains("\"memory\":[\"prefiero respuestas cortas\"]", handler.LastRequestBody);
    }

    [Fact]
    public async Task StepAsync_sends_neutral_messages_and_tools_and_maps_tool_calls()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            """{"status": "ok", "text": null, "tool_calls": [{"id": "c2", "name": "add_tags", "arguments": {"note_id": "n1", "tags": ["docker"]}, "arguments_error": null}, {"id": "c3", "name": "read_note", "arguments": null, "arguments_error": "invalid_json"}]}""");
        var parameters = JsonDocument.Parse("""{"type": "object"}""").RootElement;
        var earlierCall = new AgentToolCall("c1", "search_notes", JsonDocument.Parse("""{"query": "docker"}""").RootElement);

        var result = await CreateClient(handler).StepAsync(
            [AgentMessage.System("sys"), AgentMessage.User("etiqueta"), AgentMessage.Assistant(null, [earlierCall]), AgentMessage.ToolResult("c1", "[]")],
            [new AgentTool("search_notes", "Busca", parameters)],
            "gsk_user_key",
            "m");

        Assert.Equal(AgentStepStatus.Ok, result.Status);
        Assert.Equal(["add_tags", "read_note"], result.ToolCalls.Select(c => c.Name));
        Assert.Equal("docker", result.ToolCalls[0].Arguments!.Value.GetProperty("tags")[0].GetString());
        Assert.Equal((null, "invalid_json"), (result.ToolCalls[1].Arguments, result.ToolCalls[1].ArgumentsError));

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal("/internal/llm/step", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("docker", body.GetProperty("messages")[2].GetProperty("tool_calls")[0].GetProperty("arguments").GetProperty("query").GetString());
        Assert.Equal("c1", body.GetProperty("messages")[3].GetProperty("tool_call_id").GetString());
        Assert.False(body.GetProperty("messages")[1].TryGetProperty("tool_calls", out _));
        Assert.Equal("search_notes", body.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("tools_unsupported", AgentStepStatus.ToolsUnsupported)]
    [InlineData("tool_call_failed", AgentStepStatus.ToolCallFailed)]
    [InlineData("rate_limited", AgentStepStatus.RateLimited)]
    [InlineData("invalid_key", AgentStepStatus.InvalidKey)]
    [InlineData("something_new", AgentStepStatus.Unavailable)]
    public async Task StepAsync_maps_statuses(string wire, AgentStepStatus expected)
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, $$"""{"status": "{{wire}}"}""");

        var result = await CreateClient(handler).StepAsync([AgentMessage.User("q")], null, "k", null);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task StepAsync_and_SearchAsync_degrade_when_the_ai_service_is_unreachable()
    {
        var client = CreateClient(FakeHttpMessageHandler.Throwing(new HttpRequestException("Connection refused")));

        Assert.Equal(AgentStepStatus.Unavailable, (await client.StepAsync([AgentMessage.User("q")], null, "k", null)).Status);
        Assert.Empty(await client.SearchAsync(Guid.NewGuid(), "q", 5));
    }

    [Fact]
    public async Task SearchAsync_sends_the_users_id_and_maps_hits()
    {
        var userId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK, $$"""[{"id": "{{noteId}}", "title": null, "type": "Text", "tags": ["infra"], "snippet": "nginx…"}]""");

        var hits = await CreateClient(handler).SearchAsync(userId, "nginx", 5);

        Assert.Equal([new NoteSearchHit(noteId, null, "Text", ["infra"], "nginx…")], hits, NoteSearchHitComparer.Instance);
        Assert.Contains($"\"user_id\":\"{userId}\"", handler.LastRequestBody);
    }

    private sealed class NoteSearchHitComparer : IEqualityComparer<NoteSearchHit>
    {
        public static readonly NoteSearchHitComparer Instance = new();

        public bool Equals(NoteSearchHit? x, NoteSearchHit? y)
            => x is not null && y is not null && x with { Tags = [] } == y with { Tags = [] } && x.Tags.SequenceEqual(y.Tags);

        public int GetHashCode(NoteSearchHit obj) => obj.Id.GetHashCode();
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_sends_the_title_with_the_content()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{"status": "ok"}""");

        await CreateClient(handler).GenerateEmbeddingAsync(Guid.NewGuid(), Guid.NewGuid(), "Proxy inverso", "nginx");

        Assert.Contains("\"title\":\"Proxy inverso\"", handler.LastRequestBody);
        Assert.Contains("\"content\":\"nginx\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task AskAsync_sends_scope_and_history_to_the_ai_service()
    {
        var noteId = Guid.NewGuid();
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            """{"answer": "a", "source_note_ids": [], "grounded": true, "status": "ok"}""");

        await CreateClient(handler).AskAsync(
            Guid.NewGuid(), "q", "gsk_user_key", null, new AssistantScope(noteId, null), [new AssistantTurn("antes", "respuesta")]);

        Assert.Contains($"\"scope_note_id\":\"{noteId}\"", handler.LastRequestBody);
        Assert.Contains("\"scope_tag\":null", handler.LastRequestBody);
        Assert.Contains("\"history\":[{\"question\":\"antes\",\"answer\":\"respuesta\"}]", handler.LastRequestBody);
    }

    [Fact]
    public async Task AskAsync_maps_partial_context_scope_and_scope_unsupported()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            """{"answer": "a", "source_note_ids": [], "grounded": true, "status": "ok", "partial_context": true, "scope": {"tag": "docker"}}""");

        var answer = await CreateClient(handler).AskAsync(Guid.NewGuid(), "q", "gsk_user_key", null, new AssistantScope(null, "docker"));

        Assert.True(answer.PartialContext);
        Assert.Equal(new AssistantScope(null, "docker"), answer.Scope);

        var unsupported = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            """{"answer": "x", "source_note_ids": [], "grounded": false, "status": "scope_unsupported"}""");

        var failed = await CreateClient(unsupported).AskAsync(Guid.NewGuid(), "q", "gsk_user_key", null);

        Assert.Equal(AssistantAnswerStatus.ScopeUnsupported, failed.Status);
        Assert.Null(failed.PartialContext);
        Assert.Null(failed.Scope);
    }
}
