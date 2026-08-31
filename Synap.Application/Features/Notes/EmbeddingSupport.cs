using Microsoft.Extensions.DependencyInjection;
using Synap.Shared.Application.BackgroundJobs;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Notes;

/// <summary>
/// Shared by note creation and update - both need to (re)generate the note's embedding
/// asynchronously (specs/ai-assistant "Embedding generation"/"Embedding refreshed on edit").
/// Deletion needs no equivalent call: note_embeddings has ON DELETE CASCADE on notes.id.
/// </summary>
internal static class EmbeddingSupport
{
    public static void EnqueueGeneration(IBackgroundJobQueue backgroundJobQueue, Guid noteId, Guid userId, string content)
    {
        backgroundJobQueue.Enqueue(async (services, cancellationToken) =>
        {
            var aiServiceClient = services.GetRequiredService<IAiServiceClient>();
            await aiServiceClient.GenerateEmbeddingAsync(noteId, userId, content, cancellationToken);
        });
    }
}
