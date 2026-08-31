using Microsoft.Extensions.Logging.Abstractions;
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
            """{"answer": "You fixed it by clearing the cache.", "source_note_ids": [], "grounded": true}""");

        var client = CreateClient(handler);

        await client.AskAsync(userId, "How did I fix the build last time?");

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains(userId.ToString(), handler.LastRequestBody);
    }

    [Fact]
    public async Task AskAsync_maps_a_grounded_response_correctly()
    {
        var noteId = Guid.NewGuid();
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK,
            $$"""{"answer": "Answer text", "source_note_ids": ["{{noteId}}"], "grounded": true}""");

        var answer = await CreateClient(handler).AskAsync(Guid.NewGuid(), "question");

        Assert.True(answer.Grounded);
        Assert.Equal("Answer text", answer.Answer);
        Assert.Equal([noteId], answer.SourceNoteIds);
    }

    [Fact]
    public async Task AskAsync_degrades_gracefully_when_the_ai_service_is_unreachable()
    {
        var handler = FakeHttpMessageHandler.Throwing(new HttpRequestException("Connection refused"));

        var answer = await CreateClient(handler).AskAsync(Guid.NewGuid(), "question");

        // Never an exception bubbling up, and never a "grounded" (i.e. trustworthy) answer -
        // specs/ai-assistant "graceful handling of generation provider failure" applies just as
        // much when the AI service itself is down, not only its own LLM provider.
        Assert.False(answer.Grounded);
        Assert.Empty(answer.SourceNoteIds);
        Assert.NotEmpty(answer.Answer);
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
