using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Assistant.Queries;
using Synap.Application.Features.Settings;
using Synap.Application.Features.Settings.Commands;
using Synap.Application.Features.Settings.Queries;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Infrastructure.Persistence.Data.Users;
using Synap.Infrastructure.Services.Secrets;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;
using System.Security.Cryptography;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// byok-groq-and-settings task 4.6 - specs/user-settings "Keys isolated between users" against a
/// real Postgres: user A's key is stored encrypted, and user B never sees or uses it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GroqKeyIsolationTests
{
    private const string UserAKey = "gsk_user_a_private_key_ZZ99";

    private readonly PostgresFixture _fixture;
    private readonly ISecretProtector _protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
    private readonly IOptions<AiOptions> _options = Options.Create(new AiOptions());

    public GroqKeyIsolationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task One_users_key_is_never_visible_to_or_used_for_another_user()
    {
        var (userA, userB) = await CreateUsersAsync();
        var ai = new RecordingAiServiceClient();

        await using (var context = _fixture.CreateContext())
        {
            var save = new SaveGroqApiKeyCommandHandler(
                new UserWriteRepository(context), new ContextUnitOfWork(context), new StaticUserContext(userA), _protector, ai, _options);

            var saved = await save.Handle(new SaveGroqApiKeyCommand(UserAKey), default);
            Assert.True(saved.IsSuccess);
        }

        await using (var context = _fixture.CreateContext())
        {
            var stored = await new UserWriteRepository(context).GetByIdAsync(userA, default);
            Assert.NotNull(stored);
            Assert.NotNull(stored.GroqApiKeyEncrypted);
            Assert.DoesNotContain(UserAKey, stored.GroqApiKeyEncrypted);

            var settingsB = await new GetSettingsQueryHandler(new UserWriteRepository(context), new StaticUserContext(userB), _options)
                .Handle(new GetSettingsQuery(), default);
            Assert.False(settingsB.Value.Ai.HasGroqKey);
            Assert.Null(settingsB.Value.Ai.GroqKeyMasked);

            var answerB = await new AskAssistantQueryHandler(ai, new StaticUserContext(userB), new UserWriteRepository(context), _protector)
                .Handle(new AskAssistantQuery("¿cuál es la key de A?"), default);
            Assert.Equal(AssistantAnswerStatus.KeyMissing, answerB.Value.Status);
            Assert.Empty(ai.AskCalls);

            var answerA = await new AskAssistantQueryHandler(ai, new StaticUserContext(userA), new UserWriteRepository(context), _protector)
                .Handle(new AskAssistantQuery("pregunta"), default);
            Assert.Equal(AssistantAnswerStatus.Ok, answerA.Value.Status);
            Assert.Equal([(userA, UserAKey)], ai.AskCalls);
        }
    }

    private async Task<(Guid UserA, Guid UserB)> CreateUsersAsync()
    {
        await using var context = _fixture.CreateContext();

        var userA = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        var userB = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        context.Set<User>().AddRange(userA, userB);
        await context.SaveChangesAsync();

        return (userA.Id.Value, userB.Id.Value);
    }

    private sealed class StaticUserContext(Guid userId) : IUserContext
    {
        public Guid? UserId { get; } = userId;
    }

    private sealed class ContextUnitOfWork(SynapDbContext context) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
        public Task CommitTransactionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RollbackTransactionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingAiServiceClient : IAiServiceClient
    {
        public List<(Guid UserId, string Key)> AskCalls { get; } = [];

        public Task GenerateEmbeddingAsync(Guid noteId, Guid userId, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<RelatedNote>> GetRelatedNotesAsync(Guid noteId, Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RelatedNote>>([]);

        public Task<AssistantAnswer> AskAsync(Guid userId, string question, string groqApiKey, string? groqModel, CancellationToken cancellationToken = default)
        {
            AskCalls.Add((userId, groqApiKey));
            return Task.FromResult(new AssistantAnswer("ok", [], true, AssistantAnswerStatus.Ok));
        }

        public Task<LlmModelsResult> ListModelsAsync(string groqApiKey, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelsResult(LlmKeyStatus.Ok, ["model-a"]));
    }
}
