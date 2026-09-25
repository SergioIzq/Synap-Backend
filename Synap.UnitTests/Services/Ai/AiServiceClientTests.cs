using Microsoft.Extensions.Logging.Abstractions;
using Synap.Domain;
using Synap.Infrastructure.Services.Ai;
using System.Net;
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
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{"status": "ok", "models": ["a", "b"]}""");

        var result = await CreateClient(handler).ListModelsAsync("gsk_user_key");

        Assert.Equal(LlmKeyStatus.Ok, result.Status);
        Assert.Equal(["a", "b"], result.Models);
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
        await CreateClient(handler).GenerateEmbeddingAsync(Guid.NewGuid(), Guid.NewGuid(), "content");
    }
}
