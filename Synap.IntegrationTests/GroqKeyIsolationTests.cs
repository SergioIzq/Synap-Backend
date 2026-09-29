using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Assistant.Agent;
using Synap.Application.Features.Assistant.Queries;
using Synap.Application.Features.Settings;
using Synap.Application.Features.Settings.Commands;
using Synap.Application.Features.Settings.Queries;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Infrastructure.Persistence.Data.Memory;
using Synap.Infrastructure.Persistence.Data.Notes;
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

            var answerB = await AskHandler(ai, userB, context)
                .Handle(new AskAssistantQuery("¿cuál es la key de A?"), default);
            Assert.Equal(AssistantAnswerStatus.KeyMissing, answerB.Value.Status);
            Assert.Empty(ai.AskCalls);

            var answerA = await AskHandler(ai, userA, context)
                .Handle(new AskAssistantQuery("pregunta"), default);
            Assert.Equal(AssistantAnswerStatus.Ok, answerA.Value.Status);
            Assert.Equal([(userA, UserAKey)], ai.AskCalls);
        }
    }

    /// <summary>
    /// scoped-assistant task 2.4 - a scope never reaches another user's vault: another user's note
    /// is rejected as not found before the AI service is called, and a tag is always forwarded
    /// with the asking user's own id (the AI service then only searches that user's tags - see
    /// ai-service tests/test_isolation.py).
    /// </summary>
    [Fact]
    public async Task Scoped_questions_never_reach_another_users_notes()
    {
        var (userA, userB) = await CreateUsersAsync();
        var ai = new RecordingAiServiceClient();
        Guid userAsNote;

        await using (var context = _fixture.CreateContext())
        {
            var note = Note.Create(Synap.Shared.Domain.ValueObjects.Ids.UserId.CreateFromDatabase(userA), NoteType.Text, "Privada de A", "secreto");
            await new NoteWriteRepository(context).CreateAsync(note, default);

            var b = await new UserWriteRepository(context).GetByIdAsync(userB, default);
            b!.SetGroqApiKey(_protector.Protect("gsk_user_b_key_BB11"), "BB11");
            await context.SaveChangesAsync();
            userAsNote = note.Id.Value;
        }

        await using (var context = _fixture.CreateContext())
        {
            var askAsB = AskHandler(ai, userB, context);

            var aboutAsNote = await askAsB.Handle(new AskAssistantQuery("¿qué dice?", new AssistantScope(userAsNote, null)), default);
            Assert.True(aboutAsNote.IsFailure);
            Assert.Equal(AskAssistantQueryHandler.NoteNotFound, aboutAsNote.Error);
            Assert.Empty(ai.AskCalls);

            var aboutTag = await askAsB.Handle(new AskAssistantQuery("¿qué sé?", new AssistantScope(null, "solo-de-a")), default);
            Assert.True(aboutTag.IsSuccess);
            Assert.Equal([(userB, "gsk_user_b_key_BB11")], ai.AskCalls);
            Assert.Equal(new AssistantScope(null, "solo-de-a"), Assert.Single(ai.Scopes));
        }
    }

    /// <summary>
    /// The handler over the real repositories. Its agent never gets to run a tool here - the
    /// fake AI service reports that the model can't use them - so its sender is never called;
    /// the agent's tools are covered through the HTTP pipeline (AssistantAgentApiTests).
    /// </summary>
    private AskAssistantQueryHandler AskHandler(RecordingAiServiceClient ai, Guid userId, SynapDbContext context)
        => new(
            ai,
            new StaticUserContext(userId),
            new UserWriteRepository(context),
            _protector,
            new NoteWriteRepository(context),
            new MemoryEntryReadRepository(context),
            new AssistantAgent(ai, new UnusedSender(), new NoteReadRepository(new TestDbConnectionFactory(_fixture.ConnectionString))));

    private sealed class UnusedSender : MediatR.ISender
    {
        public Task<TResponse> Send<TResponse>(MediatR.IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : MediatR.IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(MediatR.IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    internal sealed class RecordingAiServiceClient : IAiServiceClient
    {
        public List<(Guid UserId, string Key)> AskCalls { get; } = [];
        public List<AssistantScope?> Scopes { get; } = [];
        public List<IReadOnlyList<string>?> Memories { get; } = [];
        public Queue<AgentStepResult> Steps { get; } = new();
        public List<IReadOnlyList<AgentMessage>> StepMessages { get; } = [];
        public List<Guid> SearchUserIds { get; } = [];

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
            AskCalls.Add((userId, groqApiKey));
            Scopes.Add(scope);
            Memories.Add(memory);
            return Task.FromResult(new AssistantAnswer("ok", [], true, AssistantAnswerStatus.Ok));
        }

        public Task<LlmModelsResult> ListModelsAsync(string groqApiKey, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelsResult(LlmKeyStatus.Ok, [new LlmModel("model-a", true)]));

        /// <summary>Scripted steps in order; once exhausted, the model reports that it can't use tools (RAG fallback).</summary>
        public Task<AgentStepResult> StepAsync(
            IReadOnlyList<AgentMessage> messages, IReadOnlyList<AgentTool>? tools, string groqApiKey, string? groqModel, CancellationToken cancellationToken = default)
        {
            StepMessages.Add(messages.ToList());
            return Task.FromResult(Steps.Count > 0 ? Steps.Dequeue() : AgentStepResult.Failed(AgentStepStatus.ToolsUnsupported));
        }

        public Task<IReadOnlyList<NoteSearchHit>> SearchAsync(Guid userId, string query, int limit, CancellationToken cancellationToken = default)
        {
            SearchUserIds.Add(userId);
            return Task.FromResult<IReadOnlyList<NoteSearchHit>>([]);
        }
    }
}
