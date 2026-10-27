using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using System.Collections.Concurrent;
using System.Net.Http;

namespace Synap.IntegrationTests;

/// <summary>
/// The AI service as seen by the API under test (ApiFixture). By default it behaves like an
/// unreachable service - what the API saw before this fake existed. Tests script it per user,
/// since the fixture is shared: models accepted for a key, and the assistant's steps.
/// </summary>
public sealed class ApiAiServiceFake : IAiServiceClient
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<LlmModel>> _modelsByKey = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<AgentStepResult>> _stepsByKey = new();

    // Por nota: el resultado preconfigurado de GetConnectionsAsync. Si no está configurado, devuelve Pending.
    private readonly ConcurrentDictionary<Guid, NoteConnectionsResult> _connectionsByNote = new();

    // Por usuario: la lista de pares preconfigurada para GetSimilarityPairsAsync.
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<SimilarityPair>> _pairsByUser = new();

    public ConcurrentBag<(Guid UserId, string Query)> Searches { get; } = [];
    public ConcurrentBag<(string Key, IReadOnlyList<AgentMessage> Messages)> StepRequests { get; } = [];
    public ConcurrentBag<(string Key, IReadOnlyList<string>? Memory)> Asks { get; } = [];

    /// <summary>Preconfigurar el resultado de GetConnectionsAsync para una nota.</summary>
    public void SetConnections(Guid noteId, NoteConnectionsResult result)
        => _connectionsByNote[noteId] = result;

    /// <summary>Preconfigurar los pares de similitud devueltos para un usuario.</summary>
    public void SetSimilarityPairs(Guid userId, IReadOnlyList<SimilarityPair> pairs)
        => _pairsByUser[userId] = pairs;

    /// <summary>Makes <paramref name="groqKey"/> valid, with these models.</summary>
    public void AcceptKey(string groqKey, params LlmModel[] models) => _modelsByKey[groqKey] = models;

    /// <summary>
    /// Queues the step results returned, in order, for requests made with <paramref name="groqKey"/>;
    /// once they run out, an accepted key's model reports that it can't use tools.
    /// </summary>
    public void ScriptSteps(string groqKey, params AgentStepResult[] steps)
        => _stepsByKey[groqKey] = new ConcurrentQueue<AgentStepResult>(steps);

    public Task GenerateEmbeddingAsync(Guid noteId, Guid userId, string? title, string content, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<RelatedNote>> GetRelatedNotesAsync(Guid noteId, Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RelatedNote>>([]);

    public Task<AssistantAnswer> AskAsync(
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
        Asks.Add((groqApiKey, memory));
        return Task.FromResult(_modelsByKey.ContainsKey(groqApiKey)
            ? new AssistantAnswer("Respuesta sin acciones.", [], true, AssistantAnswerStatus.Ok)
            : AssistantAnswer.Failed(AssistantAnswer.UnavailableMessage, AssistantAnswerStatus.Unavailable));
    }

    public Task<LlmModelsResult> ListModelsAsync(string groqApiKey, CancellationToken cancellationToken = default)
        => Task.FromResult(_modelsByKey.TryGetValue(groqApiKey, out var models)
            ? new LlmModelsResult(LlmKeyStatus.Ok, models)
            : LlmModelsResult.Failed(LlmKeyStatus.Unavailable));

    public Task<AgentStepResult> StepAsync(
        IReadOnlyList<AgentMessage> messages, IReadOnlyList<AgentTool>? tools, string groqApiKey, string? groqModel, CancellationToken cancellationToken = default)
    {
        StepRequests.Add((groqApiKey, messages.ToList()));
        return Task.FromResult(_stepsByKey.TryGetValue(groqApiKey, out var steps) && steps.TryDequeue(out var step)
            ? step
            : AgentStepResult.Failed(_modelsByKey.ContainsKey(groqApiKey) ? AgentStepStatus.ToolsUnsupported : AgentStepStatus.Unavailable));
    }

    public Task<IReadOnlyList<NoteSearchHit>> SearchAsync(Guid userId, string query, int limit, double? minSimilarity = null, CancellationToken cancellationToken = default)
    {
        Searches.Add((userId, query));
        return Task.FromResult<IReadOnlyList<NoteSearchHit>>([]);
    }

    public Task<NoteConnectionsResult> GetConnectionsAsync(
        Guid noteId,
        Guid userId,
        double minSimilarity,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (_connectionsByNote.TryGetValue(noteId, out var result))
        {
            return Task.FromResult(result);
        }

        throw new HttpRequestException("Servicio de análisis no disponible (fake).");
    }

    public Task<IReadOnlyList<SimilarityPair>> GetSimilarityPairsAsync(
        Guid userId,
        IReadOnlyList<Guid> anchorIds,
        IReadOnlyList<Guid> candidateIds,
        double minSimilarity,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (_pairsByUser.TryGetValue(userId, out var pairs))
        {
            return Task.FromResult(pairs);
        }

        return Task.FromResult<IReadOnlyList<SimilarityPair>>([]);
    }
}
