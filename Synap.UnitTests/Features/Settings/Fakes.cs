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
    public LlmModelsResult ModelsResult { get; set; } = new(LlmKeyStatus.Ok, ["model-a", "model-b"]);
    public AssistantAnswer Answer { get; set; } = new("respuesta", [], true, AssistantAnswerStatus.Ok);

    public List<string> ListModelsKeys { get; } = [];
    public List<(Guid UserId, string Key, string? Model)> AskCalls { get; } = [];

    public Task GenerateEmbeddingAsync(Guid noteId, Guid userId, string content, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<RelatedNote>> GetRelatedNotesAsync(Guid noteId, Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RelatedNote>>([]);

    public Task<AssistantAnswer> AskAsync(Guid userId, string question, string groqApiKey, string? groqModel, CancellationToken cancellationToken = default)
    {
        AskCalls.Add((userId, groqApiKey, groqModel));
        return Task.FromResult(Answer);
    }

    public Task<LlmModelsResult> ListModelsAsync(string groqApiKey, CancellationToken cancellationToken = default)
    {
        ListModelsKeys.Add(groqApiKey);
        return Task.FromResult(ModelsResult);
    }
}
