using Synap.Application.Features.Assistant.Agent;
using Synap.Application.Features.Assistant.Queries;
using Synap.Application.Features.Memory.Commands;
using Synap.Application.Features.Notes.Commands.AddTag;
using Synap.Application.Features.Notes.Commands.Create;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;
using Synap.UnitTests.Domain;
using Synap.UnitTests.Features.Memory;
using Synap.UnitTests.Features.Settings;
using System.Text.Json;

namespace Synap.UnitTests.Features.Assistant;

/// <summary>
/// scoped-assistant tasks 2.1/2.2 - scope validation, ownership and bookmark checks before the AI
/// service is ever called; assistant-agent-foundations task 6.3 - which path answers each question,
/// with the conversation's history and the user's memory.
/// </summary>
public class AskAssistantQueryHandlerTests
{
    private const string Key = "gsk_valid_key_a1B2";

    private readonly FakeUserRepository _users = new();
    private readonly FakeSecretProtector _protector = new();
    private readonly FakeAiServiceClient _ai = new();
    private readonly FakeNoteRepository _notes = new();
    private readonly FakeMemoryRepository _memory = new();
    private readonly User _user = UserGroqSettingsTests.NewUser();

    public AskAssistantQueryHandlerTests()
    {
        _users.Add(_user);
        _user.SetGroqApiKey(_protector.Protect(Key), Key[^4..]);
    }

    private AskAssistantQueryHandler Handler()
    {
        var context = new FakeUserContext(_user.Id.Value);
        var unitOfWork = new FakeUnitOfWork();
        var tags = new FakeTagRepository();
        var sender = new DispatchingSender(
            new CreateNoteCommandHandler(_notes, tags, unitOfWork, context, new NoopJobQueue()),
            new AddTagCommandHandler(_notes, tags, unitOfWork, context),
            new AddMemoryEntryCommandHandler(_memory, unitOfWork, context));
        var agent = new AssistantAgent(_ai, sender, new NotesView(_notes));
        return new(_ai, context, _users, _protector, _notes, _memory, agent);
    }

    private void Remember(string text) => _memory.Add(MemoryEntry.Create(UserId.CreateFromDatabase(_user.Id.Value), text).Value);

    private Note NoteOf(User owner, NoteType type = NoteType.Text)
        => _notes.Add(Note.Create(UserId.CreateFromDatabase(owner.Id.Value), type, "CORS", "Reinicia la API."));

    [Theory]
    [InlineData(true, "docker")]
    [InlineData(false, null)]
    [InlineData(false, "  # ")]
    public async Task Scope_must_name_exactly_one_note_or_tag(bool withNote, string? tag)
    {
        var scope = new AssistantScope(withNote ? NoteOf(_user).Id.Value : null, tag);

        var result = await Handler().Handle(new AskAssistantQuery("¿qué?", scope), default);

        Assert.True(result.IsFailure);
        Assert.Equal(AskAssistantQueryHandler.InvalidScope, result.Error);
        Assert.Empty(_ai.AskCalls);
    }

    [Fact]
    public async Task Another_users_note_looks_like_a_missing_one_and_never_reaches_the_ai_service()
    {
        var otherUsersNote = NoteOf(_users.Add(UserGroqSettingsTests.NewUser()));

        var foreign = await Handler().Handle(new AskAssistantQuery("¿qué?", new AssistantScope(otherUsersNote.Id.Value, null)), default);
        var missing = await Handler().Handle(new AskAssistantQuery("¿qué?", new AssistantScope(Guid.NewGuid(), null)), default);

        Assert.Equal(AskAssistantQueryHandler.NoteNotFound, foreign.Error);
        Assert.Equal(missing.Error, foreign.Error);
        Assert.Empty(_ai.AskCalls);
    }

    [Fact]
    public async Task Bookmark_scope_is_unsupported_without_calling_the_ai_service()
    {
        var bookmark = NoteOf(_user, NoteType.Bookmark);

        var result = await Handler().Handle(new AskAssistantQuery("resúmelo", new AssistantScope(bookmark.Id.Value, null)), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(AssistantAnswerStatus.ScopeUnsupported, result.Value.Status);
        Assert.Equal(AssistantAnswer.ScopeUnsupportedMessage, result.Value.Answer);
        Assert.Empty(_ai.AskCalls);
    }

    [Fact]
    public async Task Key_checks_still_come_first()
    {
        _user.ClearGroqApiKey();
        var otherUsersNote = NoteOf(_users.Add(UserGroqSettingsTests.NewUser()));

        var result = await Handler().Handle(new AskAssistantQuery("¿qué?", new AssistantScope(otherUsersNote.Id.Value, null)), default);

        Assert.Equal(AssistantAnswerStatus.KeyMissing, result.Value.Status);
        Assert.Empty(_ai.AskCalls);
    }

    [Fact]
    public async Task Own_note_is_forwarded_with_its_last_three_turns_cut_to_size()
    {
        var note = NoteOf(_user);
        var history = Enumerable.Range(0, 5)
            .Select(i => new AssistantTurn($"q{i}" + new string('?', 2_000), new string('r', 5_000)))
            .ToList();

        var result = await Handler().Handle(new AskAssistantQuery("desarrolla el punto 2", new AssistantScope(note.Id.Value, null), history), default);

        Assert.True(result.IsSuccess);
        var (scope, sent) = Assert.Single(_ai.AskScopes);
        Assert.Equal(new AssistantScope(note.Id.Value, null), scope);
        Assert.NotNull(sent);
        Assert.Equal(["q2", "q3", "q4"], sent.Select(t => t.Question[..2]));
        Assert.All(sent, t => Assert.Equal(AskAssistantQueryHandler.MaxHistoryQuestionChars, t.Question.Length));
        Assert.All(sent, t => Assert.Equal(AskAssistantQueryHandler.MaxHistoryAnswerChars, t.Answer.Length));
    }

    [Fact]
    public async Task Tag_is_normalised_like_stored_tags_and_needs_no_lookup()
    {
        var result = await Handler().Handle(new AskAssistantQuery("¿qué sé?", new AssistantScope(null, "  #docker ")), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(new AssistantScope(null, "docker"), Assert.Single(_ai.AskScopes).Scope);
    }

    [Fact]
    public async Task Scoped_questions_carry_memory_and_never_perform_actions()
    {
        Remember("prefiero respuestas cortas");

        await Handler().Handle(new AskAssistantQuery("apúntame algo", new AssistantScope(null, "docker")), default);

        Assert.Equal(["prefiero respuestas cortas"], Assert.Single(_ai.AskMemories)!);
        Assert.Equal(ActionsUnavailableReason.Scope, Assert.Single(_ai.AskActionsUnavailable));
        Assert.Empty(_ai.StepCalls);
    }

    [Fact]
    public async Task Global_question_is_answered_by_the_agent_with_history_and_memory()
    {
        Remember("uso Ubuntu");
        _ai.Steps.Enqueue(new AgentStepResult(AgentStepStatus.Ok, "Con systemctl restart nginx.", []));

        var result = await Handler().Handle(new AskAssistantQuery("¿y cómo lo reinicio?", null, [new AssistantTurn("¿cómo configuré nginx?", "Así.")]), default);

        Assert.Equal("Con systemctl restart nginx.", result.Value.Answer);
        Assert.Empty(result.Value.Actions);
        Assert.Empty(_ai.AskCalls);
        var messages = Assert.Single(_ai.StepCalls).Messages;
        Assert.Contains("- uso Ubuntu", messages[0].Content);
        Assert.Equal("¿cómo configuré nginx?", messages[1].Content);
        Assert.Equal(Key, _ai.StepCalls[0].Key);
    }

    [Fact]
    public async Task Global_question_without_action_support_is_answered_without_actions()
    {
        Remember("uso Ubuntu");
        _ai.Steps.Enqueue(AgentStepResult.Failed(AgentStepStatus.ToolsUnsupported));

        var result = await Handler().Handle(new AskAssistantQuery("apúntame algo", null, [new AssistantTurn("antes", "respuesta")]), default);

        Assert.True(result.IsSuccess);
        var (scope, history) = Assert.Single(_ai.AskScopes);
        Assert.Null(scope);
        Assert.Equal(["antes"], history!.Select(t => t.Question));
        Assert.Equal(["uso Ubuntu"], _ai.AskMemories.Single()!);
        Assert.Equal(ActionsUnavailableReason.Model, _ai.AskActionsUnavailable.Single());
    }

    [Fact]
    public async Task Actions_done_before_falling_back_are_reported_with_the_answer()
    {
        _ai.Steps.Enqueue(new AgentStepResult(AgentStepStatus.Ok, null,
            [new AgentToolCall("c1", "remember", JsonSerializer.SerializeToElement(new { text = "uso Ubuntu" }))]));
        _ai.Steps.Enqueue(AgentStepResult.Failed(AgentStepStatus.ToolCallFailed));
        _ai.Steps.Enqueue(AgentStepResult.Failed(AgentStepStatus.ToolCallFailed));

        var result = await Handler().Handle(new AskAssistantQuery("recuerda que uso Ubuntu y apunta algo"), default);

        Assert.Equal("respuesta", result.Value.Answer);
        Assert.Equal(AssistantActionType.MemorySaved, Assert.Single(result.Value.Actions).Type);
        Assert.Equal(3, _ai.StepCalls.Count);
        Assert.Single(_ai.AskCalls);
    }
}
