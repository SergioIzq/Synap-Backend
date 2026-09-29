using MediatR;
using Synap.Application.Features.Assistant.Agent;
using Synap.Application.Features.Memory.Commands;
using Synap.Application.Features.Notes.Commands.AddTag;
using Synap.Application.Features.Notes.Commands.Create;
using Synap.Domain;
using Synap.Shared.Application.BackgroundJobs;
using Synap.Shared.Domain.ValueObjects.Ids;
using Synap.UnitTests.Features.Memory;
using Synap.UnitTests.Features.Settings;
using System.Text.Json;

namespace Synap.UnitTests.Features.Assistant;

/// <summary>
/// assistant-agent-foundations task 6.2 - the tool loop with a scripted AI service and the real
/// use-case handlers over in-memory fakes, so a tool gets exactly a user action's validation.
/// </summary>
public class AssistantAgentTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private const string Key = "gsk_me";

    private readonly FakeAiServiceClient _ai = new();
    private readonly FakeNoteRepository _notes = new();
    private readonly FakeTagRepository _tags = new();
    private readonly FakeMemoryRepository _memory = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly AssistantAgent _agent;

    public AssistantAgentTests()
    {
        var context = new FakeUserContext(Me);
        var sender = new DispatchingSender(
            new CreateNoteCommandHandler(_notes, _tags, _unitOfWork, context, new NoopJobQueue()),
            new AddTagCommandHandler(_notes, _tags, _unitOfWork, context),
            new AddMemoryEntryCommandHandler(_memory, _unitOfWork, context));
        _agent = new AssistantAgent(_ai, sender, new NotesView(_notes));
    }

    private Note SeedNote(Guid owner, string title, string content)
        => _notes.Add(Note.Create(UserId.CreateFromDatabase(owner), NoteType.Text, title, content));

    private Task<AgentOutcome> AskAsync(string question = "pregunta", IReadOnlyList<AssistantTurn>? history = null, IReadOnlyList<string>? memory = null)
        => _agent.RunAsync(Me, question, history ?? [], memory ?? [], Key, "capable/model", default);

    private static AgentToolCall Call(string id, string name, object arguments)
        => new(id, name, JsonSerializer.SerializeToElement(arguments));

    private static AgentStepResult Calls(params AgentToolCall[] calls) => new(AgentStepStatus.Ok, null, calls);

    private static AgentStepResult Text(string text) => new(AgentStepStatus.Ok, text, []);

    private IReadOnlyList<AgentMessage> ToolResults(int step)
        => _ai.StepCalls[step].Messages.Where(m => m.Role == "tool").ToList();

    [Fact]
    public async Task Finds_a_note_and_tags_it()
    {
        var docker = SeedNote(Me, "Docker: volúmenes", "docker compose down -v borra los volúmenes");
        _ai.Search = (_, _, _) => [new NoteSearchHit(docker.Id.Value, docker.Title, "Text", [], "docker compose down -v…")];
        _ai.Steps.Enqueue(Calls(Call("c1", "search_notes", new { query = "volúmenes docker" })));
        _ai.Steps.Enqueue(Calls(Call("c2", "add_tags", new { note_id = docker.Id.Value, tags = new[] { "#docker" } })));
        _ai.Steps.Enqueue(Text("Hecho: he etiquetado la nota como #docker."));

        var outcome = await AskAsync("Etiqueta mi nota de volúmenes de Docker como #docker");

        var answer = outcome.Answer!;
        Assert.Equal(AssistantAnswerStatus.Ok, answer.Status);
        Assert.Equal("Hecho: he etiquetado la nota como #docker.", answer.Answer);
        Assert.Equal(["docker"], docker.Tags.Select(t => t.Name));
        var action = Assert.Single(answer.Actions);
        Assert.Equal((AssistantActionType.TagsAdded, docker.Id.Value, "Docker: volúmenes"), (action.Type, action.NoteId!.Value, action.Title));
        Assert.Equal([docker.Id.Value], answer.Sources.Select(s => s.Id));
        Assert.Equal(3, outcome.Steps);
        Assert.Equal([(Me, "volúmenes docker", 5)], _ai.SearchCalls);
        // Every step used the user's own key, and tool results went back to the model.
        Assert.All(_ai.StepCalls, c => Assert.Equal(Key, c.Key));
        Assert.Contains(docker.Id.Value.ToString(), ToolResults(1).Single().Content);
    }

    [Fact]
    public async Task Creates_a_note_with_its_tags()
    {
        _ai.Steps.Enqueue(Calls(Call("c1", "create_note", new { title = "Renovar SSL", content = "El martes renovar el certificado", tags = new[] { "infra" } })));
        _ai.Steps.Enqueue(Text("Apuntado."));

        var outcome = await AskAsync("Apúntame que el martes renuevo el SSL, #infra");

        var note = Assert.Single(_notes.All);
        Assert.Equal((Me, "Renovar SSL", "El martes renovar el certificado"), (note.UserId.Value, note.Title, note.Content));
        Assert.Equal(["infra"], note.Tags.Select(t => t.Name));
        var action = Assert.Single(outcome.Answer!.Actions);
        Assert.Equal((AssistantActionType.NoteCreated, note.Id.Value), (action.Type, action.NoteId!.Value));
        Assert.Equal(["infra"], action.Tags!);
    }

    [Fact]
    public async Task A_duplicate_create_in_one_question_makes_one_note()
    {
        var create = new { title = "Idea", content = "Resumen matinal" };
        _ai.Steps.Enqueue(Calls(Call("c1", "create_note", create), Call("c2", "create_note", create)));
        _ai.Steps.Enqueue(Text("Hecho."));

        var outcome = await AskAsync();

        Assert.Single(_notes.All);
        Assert.Single(outcome.Answer!.Actions);
        Assert.Contains("already_created", ToolResults(1)[1].Content);
    }

    [Fact]
    public async Task Stops_at_four_requests_and_offers_no_tools_on_the_last()
    {
        for (var i = 0; i < 5; i++)
        {
            _ai.Steps.Enqueue(Calls(Call($"c{i}", "search_notes", new { query = $"q{i}" })));
        }

        var outcome = await AskAsync();

        Assert.Equal(AssistantAgent.MaxSteps, _ai.StepCalls.Count);
        Assert.All(_ai.StepCalls.Take(3), c => Assert.NotNull(c.Tools));
        Assert.Null(_ai.StepCalls[3].Tools);
        Assert.StartsWith(AssistantAgent.OutOfStepsMessage, outcome.Answer!.Answer);
        Assert.Equal(AssistantAnswerStatus.Ok, outcome.Answer.Status);
    }

    [Fact]
    public async Task Invalid_arguments_get_an_error_result_and_the_loop_goes_on()
    {
        _ai.Steps.Enqueue(Calls(new AgentToolCall("c1", "search_notes", null, "invalid_json"), Call("c2", "delete_note", new { note_id = "x" })));
        _ai.Steps.Enqueue(Text("No he podido."));

        var outcome = await AskAsync();

        var results = ToolResults(1);
        Assert.Contains("invalid_arguments", results[0].Content);
        Assert.Contains("unknown_tool", results[1].Content);
        Assert.Equal("No he podido.", outcome.Answer!.Answer);
        Assert.Empty(_ai.SearchCalls);
    }

    [Fact]
    public async Task Another_users_note_is_not_found_for_reading_or_tagging()
    {
        var theirs = SeedNote(Other, "Privada", "secreto");
        _ai.Steps.Enqueue(Calls(
            Call("c1", "read_note", new { note_id = theirs.Id.Value }),
            Call("c2", "add_tags", new { note_id = theirs.Id.Value, tags = new[] { "mía" } })));
        _ai.Steps.Enqueue(Text("No existe."));

        var outcome = await AskAsync();

        Assert.All(ToolResults(1), r => Assert.Contains("not_found", r.Content));
        Assert.DoesNotContain(ToolResults(1), r => r.Content!.Contains("secreto"));
        Assert.Empty(theirs.Tags);
        Assert.Empty(outcome.Answer!.Actions);
        Assert.Empty(outcome.Answer.Sources);
    }

    [Fact]
    public async Task A_provider_failure_keeps_the_actions_already_done()
    {
        _ai.Steps.Enqueue(Calls(Call("c1", "create_note", new { title = "Nota", content = "texto" })));
        _ai.Steps.Enqueue(Calls(Call("c2", "search_notes", new { query = "texto" })));
        _ai.Steps.Enqueue(AgentStepResult.Failed(AgentStepStatus.Unavailable));

        var outcome = await AskAsync();

        Assert.Equal(AssistantAnswerStatus.Unavailable, outcome.Answer!.Status);
        Assert.Equal(AssistantAnswer.UnavailableMessage, outcome.Answer.Answer);
        Assert.Equal(AssistantActionType.NoteCreated, Assert.Single(outcome.Answer.Actions).Type);
        Assert.Single(_notes.All);
    }

    [Theory]
    [InlineData(AgentStepStatus.RateLimited, AssistantAnswerStatus.RateLimited)]
    [InlineData(AgentStepStatus.InvalidKey, AssistantAnswerStatus.InvalidKey)]
    public async Task Key_problems_are_typed(AgentStepStatus step, AssistantAnswerStatus expected)
    {
        _ai.Steps.Enqueue(AgentStepResult.Failed(step));

        Assert.Equal(expected, (await AskAsync()).Answer!.Status);
    }

    [Fact]
    public async Task A_model_without_tools_falls_back_to_answering_without_actions()
    {
        _ai.Steps.Enqueue(AgentStepResult.Failed(AgentStepStatus.ToolsUnsupported));

        var outcome = await AskAsync();

        Assert.Null(outcome.Answer);
        Assert.Empty(outcome.ActionsSoFar!);
    }

    [Fact]
    public async Task A_broken_tool_call_is_retried_once_then_falls_back()
    {
        _ai.Steps.Enqueue(AgentStepResult.Failed(AgentStepStatus.ToolCallFailed));
        _ai.Steps.Enqueue(AgentStepResult.Failed(AgentStepStatus.ToolCallFailed));

        var outcome = await AskAsync();

        Assert.Equal(2, _ai.StepCalls.Count);
        Assert.Null(outcome.Answer);
        Assert.True(outcome.Steps < AssistantAgent.MaxSteps, "the RAG answer must still fit within the cap");
    }

    [Fact]
    public async Task Remembers_and_relays_a_full_memory()
    {
        for (var i = 0; i < MemoryEntry.MaxEntriesPerUser - 1; i++)
        {
            _memory.Add(MemoryEntry.Create(UserId.CreateFromDatabase(Me), $"hecho {i}").Value);
        }

        _ai.Steps.Enqueue(Calls(Call("c1", "remember", new { text = "prefiero respuestas cortas" }), Call("c2", "remember", new { text = "uso Ubuntu" })));
        _ai.Steps.Enqueue(Text("Recordado lo primero; la memoria está llena para lo segundo."));

        var outcome = await AskAsync();

        var action = Assert.Single(outcome.Answer!.Actions);
        Assert.Equal((AssistantActionType.MemorySaved, "prefiero respuestas cortas"), (action.Type, action.Text));
        Assert.Contains("memoria está llena", ToolResults(1)[1].Content);
    }

    [Fact]
    public async Task Prompt_carries_memory_and_history_before_the_question()
    {
        _ai.Steps.Enqueue(Text("Hola."));

        await AskAsync("¿y cómo lo reinicio?", [new AssistantTurn("¿cómo configuré nginx?", "Así.")], ["prefiero respuestas cortas"]);

        var messages = _ai.StepCalls[0].Messages;
        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Contains("- prefiero respuestas cortas", messages[0].Content);
        Assert.Equal("¿y cómo lo reinicio?", messages[3].Content);
        Assert.DoesNotContain(_ai.StepCalls[0].Tools!, t => t.Name.Contains("delete"));
    }
}

internal sealed class DispatchingSender(
    CreateNoteCommandHandler createNote,
    AddTagCommandHandler addTag,
    AddMemoryEntryCommandHandler addMemory) : ISender
{
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => request switch
        {
            CreateNoteCommand c => (TResponse)(object)await createNote.Handle(c, cancellationToken),
            AddTagCommand c => (TResponse)(object)await addTag.Handle(c, cancellationToken),
            AddMemoryEntryCommand c => (TResponse)(object)await addMemory.Handle(c, cancellationToken),
            _ => throw new NotSupportedException(request.GetType().Name),
        };

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        => throw new NotSupportedException();

    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeTagRepository : ITagWriteRepository
{
    private readonly List<Tag> _tags = [];

    public Task<Tag?> GetByNameAsync(Guid userId, string name, CancellationToken cancellationToken = default)
        => Task.FromResult(_tags.FirstOrDefault(t => t.UserId.Value == userId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task<Tag?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_tags.FirstOrDefault(t => t.Id.Value == id));

    public void Add(Tag entity) => _tags.Add(entity);

    public Task CreateAsync(Tag entity, CancellationToken cancellationToken)
    {
        _tags.Add(entity);
        return Task.CompletedTask;
    }

    public void Update(Tag entity)
    {
    }

    public void Delete(Tag entity) => _tags.Remove(entity);
}

internal sealed class NoopJobQueue : IBackgroundJobQueue
{
    public void Enqueue(Func<IServiceProvider, CancellationToken, Task> job)
    {
    }
}

/// <summary>The read side over the same in-memory notes, with the real repository's ownership rule.</summary>
internal sealed class NotesView(FakeNoteRepository notes) : INoteReadRepository
{
    public Task<NoteSearchResult?> GetByIdAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var note = notes.All.FirstOrDefault(n => n.Id.Value == noteId && n.UserId.Value == userId);
        return Task.FromResult(note is null
            ? null
            : new NoteSearchResult(note.Id.Value, note.Title, note.Content, note.Type, note.FechaCreacion, note.UpdatedAt,
                note.Tags.Select(t => t.Name).ToList(), null, null, null));
    }

    public Task<PagedResult<NoteSearchResult>> SearchAsync(Guid userId, NoteSearchCriteria criteria, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> ListTagsAsync(Guid userId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
