using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.UnitTests.Features.Settings;

internal sealed class FakeUserRepository : IUserWriteRepository
{
    private readonly Dictionary<Guid, User> _users = [];

    public int UpdateCalls { get; private set; }

    public User Add(User user)
    {
        _users[user.Id.Value] = user;
        return user;
    }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(_users.GetValueOrDefault(id));

    void SergioIzq.Domain.Kernel.Interfaces.Repositories.IWriteRepository<User, UserId>.Add(User entity) => Add(entity);

    public Task CreateAsync(User entity, CancellationToken cancellationToken)
    {
        Add(entity);
        return Task.CompletedTask;
    }

    public void Update(User entity) => UpdateCalls++;

    public void Delete(User entity) => _users.Remove(entity.Id.Value);

    public List<Guid> DeletedWithAllData { get; } = [];

    public Task DeleteWithAllDataAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        DeletedWithAllData.Add(userId);
        _users.Remove(userId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCalls { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        return Task.FromResult(1);
    }

    public Task CommitTransactionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RollbackTransactionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeUserContext(Guid? userId) : IUserContext
{
    public Guid? UserId { get; } = userId;
}

/// <summary>Reversible "encryption" so tests can assert on what the handler stored.</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => $"enc({plaintext})";

    public bool TryUnprotect(string protectedValue, out string plaintext)
    {
        if (protectedValue.StartsWith("enc(") && protectedValue.EndsWith(')'))
        {
            plaintext = protectedValue[4..^1];
            return true;
        }

        plaintext = string.Empty;
        return false;
    }
}

internal sealed class FakeAiServiceClient : IAiServiceClient
{
    public LlmModelsResult ModelsResult { get; set; } = new(LlmKeyStatus.Ok, [new LlmModel("model-a", true), new LlmModel("model-b", false)]);
    public AssistantAnswer Answer { get; set; } = new("respuesta", [], true, AssistantAnswerStatus.Ok);

    public List<string> ListModelsKeys { get; } = [];
    public List<(Guid UserId, string Key, string? Model)> AskCalls { get; } = [];
    public List<(AssistantScope? Scope, IReadOnlyList<AssistantTurn>? History)> AskScopes { get; } = [];
    public List<IReadOnlyList<string>?> AskMemories { get; } = [];
    public List<ActionsUnavailableReason?> AskActionsUnavailable { get; } = [];

    /// <summary>Scripted answers for the assistant's tool loop, returned in order; Unavailable once exhausted.</summary>
    public Queue<AgentStepResult> Steps { get; } = new();
    public List<(IReadOnlyList<AgentMessage> Messages, IReadOnlyList<AgentTool>? Tools, string Key, string? Model)> StepCalls { get; } = [];

    public Func<Guid, string, int, IReadOnlyList<NoteSearchHit>> Search { get; set; } = (_, _, _) => [];
    public List<(Guid UserId, string Query, int Limit)> SearchCalls { get; } = [];

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
        AskCalls.Add((userId, groqApiKey, groqModel));
        AskScopes.Add((scope, history));
        AskMemories.Add(memory);
        AskActionsUnavailable.Add(actionsUnavailable);
        return Task.FromResult(Answer);
    }

    public Task<AgentStepResult> StepAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentTool>? tools,
        string groqApiKey,
        string? groqModel,
        CancellationToken cancellationToken = default)
    {
        // A snapshot: the agent keeps appending to its own list.
        StepCalls.Add((messages.ToList(), tools, groqApiKey, groqModel));
        return Task.FromResult(Steps.Count > 0 ? Steps.Dequeue() : AgentStepResult.Failed(AgentStepStatus.Unavailable));
    }

    public Task<IReadOnlyList<NoteSearchHit>> SearchAsync(Guid userId, string query, int limit, CancellationToken cancellationToken = default)
    {
        SearchCalls.Add((userId, query, limit));
        return Task.FromResult(Search(userId, query, limit));
    }

    public Task<LlmModelsResult> ListModelsAsync(string groqApiKey, CancellationToken cancellationToken = default)
    {
        ListModelsKeys.Add(groqApiKey);
        return Task.FromResult(ModelsResult);
    }
}

internal sealed class FakeNoteRepository : INoteWriteRepository
{
    private readonly List<Note> _notes = [];

    public IReadOnlyList<Note> All => _notes;

    public Note Add(Note note)
    {
        _notes.Add(note);
        return note;
    }

    public Task<Note?> GetOwnedByUserAsync(Guid noteId, Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult(_notes.FirstOrDefault(n => n.Id.Value == noteId && n.UserId.Value == userId));

    public Task<Note?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(_notes.FirstOrDefault(n => n.Id.Value == id));

    void SergioIzq.Domain.Kernel.Interfaces.Repositories.IWriteRepository<Note, NoteId>.Add(Note entity) => Add(entity);

    public Task CreateAsync(Note entity, CancellationToken cancellationToken)
    {
        Add(entity);
        return Task.CompletedTask;
    }

    public void Update(Note entity)
    {
    }

    public void Delete(Note entity) => _notes.Remove(entity);
}

/// <summary>Keeps the formatted text of every record, so a test can assert what was logged.</summary>
internal sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Records { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Records.Add(formatter(state, exception));
}
