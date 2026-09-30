using MediatR;
using Microsoft.Extensions.Logging;
using Synap.Application.Features.Memory.Commands;
using Synap.Application.Features.Reminders;
using Synap.Application.Features.Reminders.Commands;
using Synap.Application.Features.Notes;
using Synap.Application.Features.Notes.Commands.AddTag;
using Synap.Application.Features.Notes.Commands.Create;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Synap.Application.Features.Assistant.Agent;

/// <summary>
/// The global conversation's tool loop (assistant-agent-foundations design.md Decision 1): the
/// AI service makes one generation step at a time; this runs the tools the model asks for,
/// through the same use cases the public API uses, as the requesting user - so validation,
/// ownership and tag normalisation are exactly those of a user action, and there is no second
/// write path to the database.
/// </summary>
public sealed class AssistantAgent
{
    /// <summary>Hard cap of generation requests per question (specs/ai-assistant "Bounded generation requests per question").</summary>
    public const int MaxSteps = 4;

    /// <summary>A whole note read by the model is cut to the same budget as a scoped question's note.</summary>
    public const int ReadNoteMaxChars = 12_000;

    public const int MaxSources = 8;
    private const int SourcePreviewChars = 60;

    // Tool results are read by the model: accents and "ñ" as they are, not \u escapes.
    private static readonly JsonSerializerOptions ToolResultJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const string OutOfStepsMessage = "He hecho lo que he podido en los pasos disponibles.";

    private readonly IAiServiceClient _aiServiceClient;
    private readonly ISender _sender;
    private readonly INoteReadRepository _noteReadRepository;
    private readonly ILogger<AssistantAgent> _logger;

    public AssistantAgent(
        IAiServiceClient aiServiceClient,
        ISender sender,
        INoteReadRepository noteReadRepository,
        ILogger<AssistantAgent> logger)
    {
        _aiServiceClient = aiServiceClient;
        _sender = sender;
        _noteReadRepository = noteReadRepository;
        _logger = logger;
    }

    public async Task<AgentOutcome> RunAsync(
        Guid userId,
        string question,
        IReadOnlyList<AssistantTurn> history,
        IReadOnlyList<string> memory,
        string groqApiKey,
        string? groqModel,
        CancellationToken cancellationToken,
        string? timezone = null)
    {
        var run = new Run(userId, timezone);
        // The model needs the current moment and the user's timezone to turn "el viernes" into the
        // UTC instant set_reminder takes (assistant-reminders design.md Decision 6).
        var messages = AgentPrompt.Messages(question, history, memory, DateTime.UtcNow, timezone);
        var retriedToolCall = false;

        for (var step = 1; step <= MaxSteps; step++)
        {
            // The last allowed step offers no tools, so the model has to answer with text.
            var tools = step < MaxSteps ? AgentTools.All : null;
            var result = await _aiServiceClient.StepAsync(messages, tools, groqApiKey, groqModel, cancellationToken);

            switch (result.Status)
            {
                case AgentStepStatus.ToolsUnsupported:
                    return run.FallBackToRag(step - 1);
                case AgentStepStatus.ToolCallFailed when !retriedToolCall && step < MaxSteps - 1:
                    retriedToolCall = true;
                    continue;
                case AgentStepStatus.ToolCallFailed:
                    // The RAG answer is one more request: only if it still fits within the cap.
                    return step < MaxSteps ? run.FallBackToRag(step) : run.Answer(OutOfStepsText(run), step);
                case AgentStepStatus.InvalidKey:
                    return run.Failed(AssistantAnswer.InvalidKeyMessage, AssistantAnswerStatus.InvalidKey, step);
                case AgentStepStatus.RateLimited:
                    return run.Failed(AssistantAnswer.RateLimitedMessage, AssistantAnswerStatus.RateLimited, step);
                case AgentStepStatus.Unavailable:
                    return run.Failed(AssistantAnswer.UnavailableMessage, AssistantAnswerStatus.Unavailable, step);
            }

            if (result.ToolCalls.Count == 0 || tools is null)
            {
                // Tool calls on the last step are ignored: no more requests are allowed.
                var text = result.ToolCalls.Count == 0 ? result.Text?.Trim() : null;
                if (string.IsNullOrEmpty(text))
                {
                    return run.Answer(OutOfStepsText(run), step);
                }

                // Prose with nothing performed is the shape of the false claim: check it before
                // it reaches the user (design.md Decision 4).
                return run.Actions.Count == 0 && step < MaxSteps
                    ? await GuardClaimAsync(run, messages, text, step, groqApiKey, groqModel, cancellationToken)
                    : run.Answer(text, step);
            }

            messages.Add(AgentMessage.Assistant(result.Text, result.ToolCalls));
            foreach (var call in result.ToolCalls)
            {
                var output = await ExecuteAsync(run, call, cancellationToken);
                messages.Add(AgentMessage.ToolResult(call.Id, output.ToJsonString(ToolResultJson)));
            }
        }

        return run.Answer(OutOfStepsText(run), MaxSteps);
    }

    /// <summary>
    /// The answer claims nothing was done, or it is not delivered (specs/ai-assistant "An answer
    /// never claims an action it did not perform"). Classify, offer the tools once more so the
    /// claim can be made true, and replace it when it still is not. Both requests come out of the
    /// same <see cref="MaxSteps"/> budget, so a question never costs more than it does today.
    /// </summary>
    private async Task<AgentOutcome> GuardClaimAsync(
        Run run,
        List<AgentMessage> messages,
        string text,
        int step,
        string groqApiKey,
        string? groqModel,
        CancellationToken cancellationToken)
    {
        var classification = await _aiServiceClient.StepAsync(
            [AgentMessage.System(AgentClaimGuard.ClassifierInstructions), AgentMessage.User(AgentClaimGuard.ClassifierQuestion(text))],
            tools: null, groqApiKey, groqModel, cancellationToken);
        var steps = step + 1;

        // A classification that did not happen decides nothing: the answer goes out as it is.
        if (classification.Status != AgentStepStatus.Ok || !AgentClaimGuard.ClaimsAnAction(classification.Text))
        {
            return run.Answer(text, steps);
        }

        if (steps < MaxSteps)
        {
            messages.Add(AgentMessage.Assistant(text));
            messages.Add(AgentMessage.User(AgentClaimGuard.RetryInstruction));

            var retry = await _aiServiceClient.StepAsync(messages, AgentTools.All, groqApiKey, groqModel, cancellationToken);
            steps++;
            if (retry.Status == AgentStepStatus.Ok && retry.ToolCalls.Count > 0)
            {
                messages.Add(AgentMessage.Assistant(retry.Text, retry.ToolCalls));
                foreach (var call in retry.ToolCalls)
                {
                    var output = await ExecuteAsync(run, call, cancellationToken);
                    messages.Add(AgentMessage.ToolResult(call.Id, output.ToJsonString(ToolResultJson)));
                }

                // The claim is now true: it is the user's answer, and the action is reported with it.
                if (run.Actions.Count > 0)
                {
                    return run.Answer(text, steps);
                }
            }
        }

        // Observable on purpose: a guard nobody can see firing is the failure this change exists
        // to stop (design.md - Migration Plan).
        _logger.LogWarning(
            "Assistant answer claimed an action that was not performed; the claim was replaced. User {UserId}, {Steps} generation requests.",
            run.UserId, steps);

        return run.Answer(AgentClaimGuard.CouldNotDoItMessage, steps);
    }

    private static string OutOfStepsText(Run run)
        => run.Actions.Count == 0
            ? $"{OutOfStepsMessage} No he llegado a una respuesta; prueba a preguntarlo de forma más concreta."
            : $"{OutOfStepsMessage} Esto es lo que he hecho:";

    // ---- Tools ----

    private async Task<JsonObject> ExecuteAsync(Run run, AgentToolCall call, CancellationToken cancellationToken)
    {
        if (call.Arguments is not { ValueKind: JsonValueKind.Object } args)
        {
            return Error("invalid_arguments");
        }

        return call.Name switch
        {
            AgentTools.SearchNotes => await SearchAsync(run, args, cancellationToken),
            AgentTools.ReadNote => await ReadAsync(run, args, cancellationToken),
            AgentTools.CreateNote => await CreateAsync(run, args, cancellationToken),
            AgentTools.AddTags => await AddTagsAsync(run, args, cancellationToken),
            AgentTools.Remember => await RememberAsync(run, args, cancellationToken),
            AgentTools.SetReminder => await SetReminderAsync(run, args, cancellationToken),
            _ => Error("unknown_tool"),
        };
    }

    private async Task<JsonObject> SearchAsync(Run run, JsonElement args, CancellationToken cancellationToken)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return Error("invalid_arguments");
        }

        var limit = Math.Clamp(GetInt(args, "limit") ?? 5, 1, AgentTools.MaxSearchLimit);
        var hits = await _aiServiceClient.SearchAsync(run.UserId, query, limit, cancellationToken);

        var notes = new JsonArray();
        foreach (var hit in hits)
        {
            run.AddSource(hit.Id, hit.Title, hit.Snippet);
            notes.Add(new JsonObject
            {
                ["id"] = hit.Id.ToString(),
                ["title"] = hit.Title,
                ["type"] = hit.Type,
                ["tags"] = new JsonArray(hit.Tags.Select(t => (JsonNode)JsonValue.Create(t)).ToArray()),
                ["snippet"] = hit.Snippet,
            });
        }

        return new JsonObject { ["notes"] = notes };
    }

    private async Task<JsonObject> ReadAsync(Run run, JsonElement args, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetString(args, "note_id"), out var noteId))
        {
            return Error("not_found");
        }

        // Another user's note is exactly a missing one (specs/ai-assistant "Assistant actions never cross users").
        var note = await _noteReadRepository.GetByIdAsync(run.UserId, noteId, cancellationToken);
        if (note is null)
        {
            return Error("not_found");
        }

        run.AddSource(note.Id, note.Title, note.Content);
        var truncated = note.Content.Length > ReadNoteMaxChars;
        return new JsonObject
        {
            ["id"] = note.Id.ToString(),
            ["title"] = note.Title,
            ["type"] = note.Type.ToString(),
            ["tags"] = new JsonArray(note.Tags.Select(t => (JsonNode)JsonValue.Create(t)).ToArray()),
            ["content"] = truncated ? note.Content[..ReadNoteMaxChars] : note.Content,
            ["truncated"] = truncated,
        };
    }

    private async Task<JsonObject> CreateAsync(Run run, JsonElement args, CancellationToken cancellationToken)
    {
        var title = GetString(args, "title")?.Trim();
        var content = GetString(args, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            return Error("El contenido de la nota no puede estar vacío.");
        }

        // The model sometimes repeats a call after an ambiguous result: one note, not two.
        var key = (title ?? string.Empty, content.Trim());
        if (run.CreatedNotes.TryGetValue(key, out var existing))
        {
            return new JsonObject { ["id"] = existing.ToString(), ["title"] = title, ["already_created"] = true };
        }

        var type = string.Equals(GetString(args, "type"), "Code", StringComparison.OrdinalIgnoreCase) ? NoteType.CodeSnippet : NoteType.Text;
        var tags = GetStrings(args, "tags");
        var created = await _sender.Send(new CreateNoteCommand(type, title, content, tags), cancellationToken);
        if (created.IsFailure)
        {
            return Error(created.Error.Message);
        }

        run.CreatedNotes[key] = created.Value;
        var normalizedTags = TagAssignment.Normalize(tags).Value;
        run.Actions.Add(new AssistantAction(AssistantActionType.NoteCreated, created.Value, NoteLabel(title, content), Tags: normalizedTags));
        return new JsonObject { ["id"] = created.Value.ToString(), ["title"] = title };
    }

    private async Task<JsonObject> AddTagsAsync(Run run, JsonElement args, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetString(args, "note_id"), out var noteId))
        {
            return Error("not_found");
        }

        var tags = TagAssignment.Normalize(GetStrings(args, "tags"));
        if (tags.IsFailure || tags.Value.Count == 0)
        {
            return Error(tags.IsFailure ? tags.Error.Message : "Indica al menos una etiqueta.");
        }

        var note = await _noteReadRepository.GetByIdAsync(run.UserId, noteId, cancellationToken);
        if (note is null)
        {
            return Error("not_found");
        }

        foreach (var tag in tags.Value)
        {
            var added = await _sender.Send(new AddTagCommand(noteId, tag), cancellationToken);
            if (added.IsFailure)
            {
                return Error(added.Error.Message);
            }
        }

        run.Actions.Add(new AssistantAction(AssistantActionType.TagsAdded, noteId, NoteLabel(note.Title, note.Content), Tags: tags.Value));
        return new JsonObject
        {
            ["id"] = noteId.ToString(),
            ["tags"] = new JsonArray(note.Tags.Concat(tags.Value).Distinct(StringComparer.OrdinalIgnoreCase).Select(t => (JsonNode)JsonValue.Create(t)).ToArray()),
        };
    }

    private async Task<JsonObject> RememberAsync(Run run, JsonElement args, CancellationToken cancellationToken)
    {
        var saved = await _sender.Send(new AddMemoryEntryCommand(GetString(args, "text")), cancellationToken);
        if (saved.IsFailure)
        {
            // The model relays it: "memoria llena, borra alguno desde Configuración", "demasiado largo"...
            return Error(saved.Error.Message);
        }

        run.Actions.Add(new AssistantAction(AssistantActionType.MemorySaved, Text: saved.Value.Text));
        return new JsonObject { ["id"] = saved.Value.Id.ToString(), ["text"] = saved.Value.Text };
    }

    private async Task<JsonObject> SetReminderAsync(Run run, JsonElement args, CancellationToken cancellationToken)
    {
        // The moment arrives as the user said it and is resolved here, against their timezone
        // (specs/ai-assistant "Reminder moments resolved in the user's timezone"). Wording that
        // names no moment creates nothing: the model is told to ask, never to invent one
        // ("Moment that cannot be resolved").
        var wording = GetString(args, "when");
        if (ReminderWording.Resolve(wording, DateTime.UtcNow, run.Timezone) is not { } dueAt)
        {
            return Error("No he entendido para cuándo. Pregunta al usuario qué día y a qué hora quiere el aviso.");
        }

        Guid? noteId = Guid.TryParse(GetString(args, "note_id"), out var parsedNoteId) ? parsedNoteId : null;
        var created = await _sender.Send(
            new CreateReminderCommand(GetString(args, "text"), dueAt, noteId, GetString(args, "recurrence")), cancellationToken);
        if (created.IsFailure)
        {
            // The model relays it: "tiene que estar en el futuro", "nota no encontrada"...
            return Error(created.Error.Message);
        }

        run.Actions.Add(new AssistantAction(AssistantActionType.ReminderCreated, created.Value.NoteId, Text: created.Value.Text)
        {
            DueAt = created.Value.DueAt,
            Recurrence = created.Value.Recurrence,
        });

        return new JsonObject
        {
            ["id"] = created.Value.Id.ToString(),
            ["text"] = created.Value.Text,
            // Echoed back in the user's own words so the model states the same moment it set.
            ["due_at"] = created.Value.DueAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["due_at_local"] = ReminderMessage.Moment(created.Value.DueAt, run.Timezone),
            ["recurrence"] = created.Value.Recurrence,
        };
    }

    // ---- Helpers ----

    private static JsonObject Error(string error) => new() { ["error"] = error };

    private static string? GetString(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static List<string> GetStrings(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : [];

    /// <summary>A note's title, or a short preview of its text when it has none (like answer sources).</summary>
    internal static string NoteLabel(string? title, string text)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= SourcePreviewChars ? flat : flat[..SourcePreviewChars].TrimEnd() + "…";
    }

    /// <summary>What one question has gathered so far.</summary>
    private sealed class Run(Guid userId, string? timezone)
    {
        private readonly List<AssistantSource> _sources = [];

        public Guid UserId { get; } = userId;
        public string? Timezone { get; } = timezone;
        public List<AssistantAction> Actions { get; } = [];
        public Dictionary<(string Title, string Content), Guid> CreatedNotes { get; } = [];

        public void AddSource(Guid id, string? title, string text)
        {
            if (_sources.Count < MaxSources && _sources.All(s => s.Id != id))
            {
                _sources.Add(new AssistantSource(id, NoteLabel(title, text)));
            }
        }

        public AgentOutcome Answer(string text, int steps)
            => new(new AssistantAnswer(text, _sources.Select(s => s.Id).ToList(), _sources.Count > 0, AssistantAnswerStatus.Ok)
            {
                Sources = _sources.ToList(),
                Actions = Actions.ToList(),
            }, steps);

        public AgentOutcome Failed(string message, AssistantAnswerStatus status, int steps)
            => new(AssistantAnswer.Failed(message, status) with { Actions = Actions.ToList() }, steps);

        public AgentOutcome FallBackToRag(int steps) => new(null, steps, Actions.ToList());
    }
}

/// <summary>
/// <see cref="Answer"/> is null when the question must be answered without actions (RAG) -
/// the model can't use tools - keeping any <see cref="ActionsSoFar"/> to report with that answer.
/// <see cref="Steps"/> is how many generation requests were made.
/// </summary>
public sealed record AgentOutcome(AssistantAnswer? Answer, int Steps, IReadOnlyList<AssistantAction>? ActionsSoFar = null);
