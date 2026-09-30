using MediatR;
using Synap.Application.Features.Assistant.Agent;
using Synap.Application.Features.Memory.Commands;
using Synap.Application.Features.Notes.Commands.AddTag;
using Synap.Application.Features.Notes.Commands.Create;
using Synap.Application.Features.Reminders.Commands;
using Synap.Domain;
using Synap.Shared.Application.BackgroundJobs;
using Synap.Shared.Domain.ValueObjects.Ids;
using Synap.UnitTests.Features.Memory;
using Synap.UnitTests.Features.Reminders;
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
    private readonly FakeReminderRepository _reminders = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly RecordingLogger<AssistantAgent> _logger = new();
    private readonly AssistantAgent _agent;

    public AssistantAgentTests()
    {
        var context = new FakeUserContext(Me);
        var sender = new DispatchingSender(
            new CreateNoteCommandHandler(_notes, _tags, _unitOfWork, context, new NoopJobQueue()),
            new AddTagCommandHandler(_notes, _tags, _unitOfWork, context),
            new AddMemoryEntryCommandHandler(_memory, _unitOfWork, context),
            new CreateReminderCommandHandler(_reminders, new NotesView(_notes), _unitOfWork, context));
        _agent = new AssistantAgent(_ai, sender, new NotesView(_notes), _logger);
    }

    private Note SeedNote(Guid owner, string title, string content)
        => _notes.Add(Note.Create(UserId.CreateFromDatabase(owner), NoteType.Text, title, content));

    private Task<AgentOutcome> AskAsync(
        string question = "pregunta",
        IReadOnlyList<AssistantTurn>? history = null,
        IReadOnlyList<string>? memory = null,
        string? timezone = null)
        => _agent.RunAsync(Me, question, history ?? [], memory ?? [], Key, "capable/model", default, timezone);

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


/// <summary>assistant-reminders tasks 7.1 and 7.3 to 7.5 - set_reminder inside the tool loop.</summary>
public class AssistantAgentReminderTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private const string Key = "gsk_me";
    private const string Madrid = "Europe/Madrid";

    private readonly FakeAiServiceClient _ai = new();
    private readonly FakeNoteRepository _notes = new();
    private readonly FakeTagRepository _tags = new();
    private readonly FakeMemoryRepository _memory = new();
    private readonly FakeReminderRepository _reminders = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly RecordingLogger<AssistantAgent> _logger = new();
    private readonly AssistantAgent _agent;

    public AssistantAgentReminderTests()
    {
        var context = new FakeUserContext(Me);
        var sender = new DispatchingSender(
            new CreateNoteCommandHandler(_notes, _tags, _unitOfWork, context, new NoopJobQueue()),
            new AddTagCommandHandler(_notes, _tags, _unitOfWork, context),
            new AddMemoryEntryCommandHandler(_memory, _unitOfWork, context),
            new CreateReminderCommandHandler(_reminders, new NotesView(_notes), _unitOfWork, context));
        _agent = new AssistantAgent(_ai, sender, new NotesView(_notes), _logger);
    }

    private Task<AgentOutcome> AskAsync(string question = "recuérdame algo", string? timezone = Madrid)
        => _agent.RunAsync(Me, question, [], [], Key, "capable/model", default, timezone);

    private static AgentToolCall Call(string name, object arguments)
        => new("call-1", name, JsonSerializer.SerializeToElement(arguments));

    private void Script(params AgentStepResult[] steps)
    {
        foreach (var step in steps)
        {
            _ai.Steps.Enqueue(step);
        }
    }

    private static AgentStepResult Calls(params AgentToolCall[] calls) => new(AgentStepStatus.Ok, null, calls);

    private static AgentStepResult Text(string text) => new(AgentStepStatus.Ok, text, []);

    private static string Iso(DateTime moment) => moment.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    // ---- The prompt's clock (task 7.1) ----

    [Fact]
    public async Task The_prompt_states_the_current_moment_and_the_users_timezone()
    {
        Script(Text("hola"));

        await AskAsync(timezone: Madrid);

        var system = _ai.StepCalls[0].Messages[0].Content!;
        Assert.Contains("Europe/Madrid", system);
        Assert.Contains(DateTime.UtcNow.ToString("yyyy-MM-dd"), system);
        // The model is told to hand over the wording, not to work the moment out itself
        // (observable-failures design.md Decision 5): Synap resolves it.
        Assert.Contains("in their own words", system);
        Assert.DoesNotContain("ISO 8601", system);
    }

    [Fact]
    public async Task Without_a_timezone_the_prompt_falls_back_to_utc()
    {
        Script(Text("hola"));

        await AskAsync(timezone: null);

        Assert.Contains("UTC", _ai.StepCalls[0].Messages[0].Content!);
    }

    // ---- The tool (task 7.3) ----

    [Fact]
    public async Task The_tool_is_offered_to_the_model_with_a_parseable_schema()
    {
        Script(Text("hola"));

        await AskAsync();

        var tool = Assert.Single(_ai.StepCalls[0].Tools!, t => t.Name == "set_reminder");
        var required = tool.Parameters.GetProperty("required").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(["text", "when"], required);
        var properties = tool.Parameters.GetProperty("properties");
        Assert.True(properties.TryGetProperty("note_id", out _));
        Assert.True(properties.TryGetProperty("recurrence", out _));
        Assert.Contains("never on your own initiative", tool.Description);
    }

    // ---- Setting a reminder (task 7.4) ----

    [Fact]
    public async Task A_reminder_is_created_through_the_same_use_case_a_user_would()
    {
        var moment = DateTime.UtcNow.AddDays(2);
        Script(
            Calls(Call("set_reminder", new { text = "Renovar el certificado SSL", when = Iso(moment) })),
            Text("Hecho, te aviso el viernes a las 9:00."));

        var outcome = await AskAsync();

        var stored = Assert.Single(_reminders.All);
        Assert.Equal("Renovar el certificado SSL", stored.Text);
        Assert.Equal(Me, stored.UserId.Value);
        Assert.True(stored.IsPending);
        var action = Assert.Single(outcome.Answer!.Actions);
        Assert.Equal(AssistantActionType.ReminderCreated, action.Type);
        Assert.Equal("Renovar el certificado SSL", action.Text);
        Assert.Equal(stored.DueAt, action.DueAt);
    }

    [Fact]
    public async Task A_recurring_reminder_carries_its_recurrence_into_the_action()
    {
        Script(
            Calls(Call("set_reminder", new { text = "Revisar copias", when = Iso(DateTime.UtcNow.AddDays(1)), recurrence = "weekly:0" })),
            Text("Todos los lunes."));

        var outcome = await AskAsync();

        Assert.Equal("weekly:0", Assert.Single(_reminders.All).Recurrence?.ToString());
        Assert.Equal("weekly:0", Assert.Single(outcome.Answer!.Actions).Recurrence);
    }

    [Fact]
    public async Task A_reminder_can_be_linked_to_one_of_the_users_notes()
    {
        var note = _notes.Add(Note.Create(UserId.CreateFromDatabase(Me), NoteType.Text, "Volúmenes de Docker", "contenido"));
        Script(
            Calls(Call("set_reminder", new { text = "Revisar esto", when = Iso(DateTime.UtcNow.AddDays(1)), note_id = note.Id.Value.ToString() })),
            Text("Hecho."));

        var outcome = await AskAsync();

        Assert.Equal(note.Id, Assert.Single(_reminders.All).NoteId);
        Assert.Equal(note.Id.Value, Assert.Single(outcome.Answer!.Actions).NoteId);
    }

    [Fact]
    public async Task A_note_that_is_not_the_users_is_reported_as_missing_and_nothing_is_created()
    {
        var theirNote = _notes.Add(Note.Create(UserId.CreateFromDatabase(Other), NoteType.Text, "Su nota", "contenido"));
        Script(
            Calls(Call("set_reminder", new { text = "Espiando", when = Iso(DateTime.UtcNow.AddDays(1)), note_id = theirNote.Id.Value.ToString() })),
            Text("No he encontrado esa nota."));

        var outcome = await AskAsync();

        Assert.Empty(_reminders.All);
        Assert.Empty(outcome.Answer!.Actions);
    }

    [Fact]
    public async Task A_moment_in_the_past_is_refused_and_the_reason_goes_back_to_the_model()
    {
        Script(
            Calls(Call("set_reminder", new { text = "Tarde", when = Iso(DateTime.UtcNow.AddDays(-1)) })),
            Text("Ese momento ya ha pasado, dime otro."));

        var outcome = await AskAsync();

        Assert.Empty(_reminders.All);
        Assert.Empty(outcome.Answer!.Actions);
        Assert.Contains("futuro", _ai.StepCalls[1].Messages.Last(m => m.Role == "tool").Content!);
    }

    /// <summary>
    /// The model now passes the user's own words and Synap resolves them
    /// (observable-failures design.md Decision 5), so what used to be a refusal is a reminder.
    /// </summary>
    [Fact]
    public async Task The_moment_arrives_in_the_users_words_and_is_resolved_here()
    {
        Script(
            Calls(Call("set_reminder", new { text = "Llamar al banco", when = "mañana a las 9" })),
            Text("Hecho."));

        var outcome = await AskAsync();

        var reminder = Assert.Single(_reminders.All);
        Assert.Equal("Llamar al banco", reminder.Text);
        Assert.True(reminder.DueAt > DateTime.UtcNow);
        Assert.Single(outcome.Answer!.Actions);
    }

    /// <summary>
    /// specs/ai-assistant "Time today honoured", end to end: the wording travels from the model,
    /// through the resolver, to a reminder in the user's own clock. The reminder that started
    /// this change was asked for "hoy a las 20:20" and, had it been created at all, 20:20 read as
    /// UTC would have arrived at 22:20 on the user's phone.
    /// </summary>
    [Fact]
    public async Task The_wording_is_resolved_against_the_users_timezone_not_utc()
    {
        Script(
            Calls(Call("set_reminder", new { text = "Hacer algo", when = "mañana a las 20:20" })),
            Text("Listo."));

        var outcome = await AskAsync(timezone: Madrid);

        var reminder = Assert.Single(_reminders.All);
        var tomorrowInMadrid = UserClock.ToLocal(DateTime.UtcNow, Madrid).Date.AddDays(1).Add(new TimeSpan(20, 20, 0));
        Assert.Equal(UserClock.ToUtc(tomorrowInMadrid, Madrid), reminder.DueAt);

        // The same moment goes back to the model in the user's words, so the answer can state it.
        var toolResult = _ai.StepCalls[1].Messages.Last(m => m.Role == "tool").Content!;
        Assert.Contains("20:20", toolResult);

        var action = Assert.Single(outcome.Answer!.Actions);
        Assert.Equal(reminder.DueAt, action.DueAt);
    }

    /// <summary>
    /// specs/ai-assistant "Moment that cannot be resolved": nothing is created and the model is
    /// told to ask, rather than being left to invent a moment nobody chose.
    /// </summary>
    [Fact]
    public async Task Wording_that_names_no_moment_creates_nothing_and_asks()
    {
        Script(
            Calls(Call("set_reminder", new { text = "Cuando sea", when = "cuando pueda" })),
            Text("¿Qué día y a qué hora quieres que te avise?"));

        var outcome = await AskAsync();

        Assert.Empty(_reminders.All);
        Assert.Contains("No he entendido para cuándo", _ai.StepCalls[1].Messages.Last(m => m.Role == "tool").Content!);
        Assert.Empty(outcome.Answer!.Actions);
    }

    [Fact]
    public async Task An_invalid_recurrence_is_refused()
    {
        Script(
            Calls(Call("set_reminder", new { text = "Cada dos martes", when = Iso(DateTime.UtcNow.AddDays(1)), recurrence = "el tercer martes" })),
            Text("No puedo con esa repetición."));

        await AskAsync();

        Assert.Empty(_reminders.All);
    }

    [Fact]
    public async Task The_tool_result_tells_the_model_the_moment_it_settled_on_in_local_time()
    {
        var moment = new DateTime(2027, 1, 8, 8, 0, 0, DateTimeKind.Utc);
        Script(
            Calls(Call("set_reminder", new { text = "Llamar al banco", when = Iso(moment) })),
            Text("El viernes 8 de enero a las 09:00."));

        await AskAsync();

        var toolResult = _ai.StepCalls[1].Messages.Last(m => m.Role == "tool").Content!;
        // 08:00 UTC in January is 09:00 in Madrid.
        Assert.Contains("09:00", toolResult);
        Assert.Contains("enero", toolResult);
    }
}

internal sealed class DispatchingSender(
    CreateNoteCommandHandler createNote,
    AddTagCommandHandler addTag,
    AddMemoryEntryCommandHandler addMemory,
    CreateReminderCommandHandler? createReminder = null) : ISender
{
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => request switch
        {
            CreateNoteCommand c => (TResponse)(object)await createNote.Handle(c, cancellationToken),
            AddTagCommand c => (TResponse)(object)await addTag.Handle(c, cancellationToken),
            AddMemoryEntryCommand c => (TResponse)(object)await addMemory.Handle(c, cancellationToken),
            CreateReminderCommand c when createReminder is not null => (TResponse)(object)await createReminder.Handle(c, cancellationToken),
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

/// <summary>
/// observable-failures tasks 5.1 to 5.4 - specs/ai-assistant "An answer never claims an action it
/// did not perform". The failure that motivated it: "Listo, te recuerdo hoy a las 20:20" delivered
/// word for word while set_reminder was never called and nothing was created.
/// </summary>
public class AssistantAgentClaimGuardTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private const string Key = "gsk_me";
    private const string Claim = "Listo, te recuerdo hoy a las 20:20.";

    private readonly FakeAiServiceClient _ai = new();
    private readonly FakeNoteRepository _notes = new();
    private readonly FakeTagRepository _tags = new();
    private readonly FakeMemoryRepository _memory = new();
    private readonly FakeReminderRepository _reminders = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly RecordingLogger<AssistantAgent> _logger = new();
    private readonly AssistantAgent _agent;

    public AssistantAgentClaimGuardTests()
    {
        var context = new FakeUserContext(Me);
        var sender = new DispatchingSender(
            new CreateNoteCommandHandler(_notes, _tags, _unitOfWork, context, new NoopJobQueue()),
            new AddTagCommandHandler(_notes, _tags, _unitOfWork, context),
            new AddMemoryEntryCommandHandler(_memory, _unitOfWork, context),
            new CreateReminderCommandHandler(_reminders, new NotesView(_notes), _unitOfWork, context));
        _agent = new AssistantAgent(_ai, sender, new NotesView(_notes), _logger);
    }

    private Task<AgentOutcome> AskAsync(string question = "recuérdame hoy a las 20:20 sacar la basura")
        => _agent.RunAsync(Me, question, [], [], Key, "capable/model", default, "Europe/Madrid");

    private void Script(params AgentStepResult[] steps)
    {
        foreach (var step in steps)
        {
            _ai.Steps.Enqueue(step);
        }
    }

    private static AgentStepResult Text(string text) => new(AgentStepStatus.Ok, text, []);

    private static AgentStepResult Calls(params AgentToolCall[] calls) => new(AgentStepStatus.Ok, null, calls);

    private static AgentToolCall Call(string name, object arguments)
        => new("c1", name, JsonSerializer.SerializeToElement(arguments));

    private bool Classified(int step)
        => _ai.StepCalls[step].Messages[0].Content == AgentClaimGuard.ClassifierInstructions;

    // ---- Task 5.1: the classification, and only when nothing was performed ----

    [Fact]
    public async Task An_answer_with_no_actions_is_classified_before_it_is_delivered()
    {
        Script(Text(Claim), Text("NO"));

        var outcome = await AskAsync();

        Assert.True(Classified(1), "the second request must be the classification");
        Assert.Contains(Claim, _ai.StepCalls[1].Messages[1].Content);
        // The classifier is asked, not offered tools: it judges, it does not act.
        Assert.Null(_ai.StepCalls[1].Tools);
        Assert.Equal(Key, _ai.StepCalls[1].Key);
        // A negative classification changes nothing: the answer is the model's own.
        Assert.Equal(Claim, outcome.Answer!.Answer);
    }

    [Fact]
    public async Task An_answer_that_performed_an_action_is_not_classified()
    {
        Script(
            Calls(Call("create_note", new { title = "Basura", content = "Sacar la basura" })),
            Text("Apuntado."));

        var outcome = await AskAsync();

        Assert.Equal(2, _ai.StepCalls.Count);
        Assert.Equal("Apuntado.", outcome.Answer!.Answer);
        Assert.Single(outcome.Answer.Actions);
    }

    [Fact]
    public async Task An_answer_with_no_text_is_not_classified()
    {
        Script(Text("   "));

        await AskAsync();

        Assert.Single(_ai.StepCalls);
    }

    [Fact]
    public async Task A_classification_that_could_not_be_made_leaves_the_answer_alone()
    {
        Script(Text(Claim), AgentStepResult.Failed(AgentStepStatus.RateLimited));

        var outcome = await AskAsync();

        Assert.Equal(Claim, outcome.Answer!.Answer);
        Assert.Equal(AssistantAnswerStatus.Ok, outcome.Answer.Status);
    }

    // ---- Task 5.2: one retry, inside the cap ----

    [Fact]
    public async Task A_claimed_action_is_retried_once_and_the_claim_stands_when_it_is_performed()
    {
        Script(
            Text(Claim),
            Text("YES"),
            Calls(Call("set_reminder", new { text = "sacar la basura", when = "hoy a las 20:20" })));

        var outcome = await AskAsync();

        // The retry is offered the tools and told the action has not happened yet.
        Assert.NotNull(_ai.StepCalls[2].Tools);
        Assert.Contains("no action", _ai.StepCalls[2].Messages[^1].Content);
        Assert.Equal(Claim, _ai.StepCalls[2].Messages[^2].Content);

        Assert.Single(_reminders.All);
        var action = Assert.Single(outcome.Answer!.Actions);
        Assert.Equal(AssistantActionType.ReminderCreated, action.Type);
        // The claim was true once the action happened, so it is what the user reads.
        Assert.Equal(Claim, outcome.Answer.Answer);
        Assert.Equal(3, outcome.Steps);
    }

    [Fact]
    public async Task The_guard_never_takes_a_question_past_the_request_cap()
    {
        Script(
            Calls(Call("search_notes", new { query = "basura" })),
            Text(Claim),
            Text("YES"),
            Text("Sigo sin hacerlo."),
            Text("una más"));

        var outcome = await AskAsync();

        Assert.True(_ai.StepCalls.Count <= AssistantAgent.MaxSteps, "the guard must fit inside the existing budget");
        Assert.Equal(AssistantAgent.MaxSteps, outcome.Steps);
    }

    [Fact]
    public async Task With_no_room_to_retry_the_claim_is_replaced_without_one()
    {
        Script(
            Calls(Call("search_notes", new { query = "basura" })),
            Calls(Call("search_notes", new { query = "cubo" })),
            Text(Claim),
            Text("YES"));

        var outcome = await AskAsync();

        Assert.Equal(AssistantAgent.MaxSteps, _ai.StepCalls.Count);
        Assert.Equal(AgentClaimGuard.CouldNotDoItMessage, outcome.Answer!.Answer);
    }

    // ---- Task 5.3: the claim is replaced, never delivered ----

    [Fact]
    public async Task A_claim_the_retry_does_not_make_true_never_reaches_the_user()
    {
        Script(Text(Claim), Text("YES"), Text("Ya está hecho, te aviso a las 20:20."));

        var outcome = await AskAsync();

        Assert.Equal(AgentClaimGuard.CouldNotDoItMessage, outcome.Answer!.Answer);
        Assert.DoesNotContain("20:20", outcome.Answer.Answer);
        Assert.DoesNotContain("Listo", outcome.Answer.Answer);
        Assert.Empty(outcome.Answer.Actions);
        Assert.Empty(_reminders.All);
        Assert.Equal(AssistantAnswerStatus.Ok, outcome.Answer.Status);
    }

    [Fact]
    public async Task A_retry_whose_action_fails_is_still_not_reported_as_done()
    {
        // A reminder in the past: the command refuses it, so nothing is performed.
        Script(
            Text(Claim),
            Text("YES"),
            Calls(Call("set_reminder", new { text = "sacar la basura", when = "cuando pueda" })));

        var outcome = await AskAsync();

        Assert.Empty(_reminders.All);
        Assert.Equal(AgentClaimGuard.CouldNotDoItMessage, outcome.Answer!.Answer);
    }

    // ---- Task 5.4: the guard is itself observable ----

    [Fact]
    public async Task Replacing_a_claim_is_recorded()
    {
        Script(Text(Claim), Text("YES"), Text("Hecho."));

        await AskAsync();

        var record = Assert.Single(_logger.Records, r => r.Contains("claimed an action"));
        Assert.Contains(Me.ToString(), record);
    }

    [Fact]
    public async Task A_delivered_answer_records_nothing()
    {
        Script(Text(Claim), Text("NO"));

        await AskAsync();

        Assert.Empty(_logger.Records);
    }
}
