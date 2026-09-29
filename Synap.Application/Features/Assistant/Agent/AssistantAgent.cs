using MediatR;
using Synap.Application.Features.Memory.Commands;
using Synap.Application.Features.Notes;
using Synap.Application.Features.Notes.Commands.AddTag;
using Synap.Application.Features.Notes.Commands.Create;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;
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

    public AssistantAgent(IAiServiceClient aiServiceClient, ISender sender, INoteReadRepository noteReadRepository)
    {
        _aiServiceClient = aiServiceClient;
        _sender = sender;
        _noteReadRepository = noteReadRepository;
    }

    public async Task<AgentOutcome> RunAsync(
        Guid userId,
        string question,
        IReadOnlyList<AssistantTurn> history,
        IReadOnlyList<string> memory,
        string groqApiKey,
        string? groqModel,
        CancellationToken cancellationToken)
    {
        var run = new Run(userId);
        var messages = AgentPrompt.Messages(question, history, memory);
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
                return run.Answer(string.IsNullOrEmpty(text) ? OutOfStepsText(run) : text, step);
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
    private sealed class Run(Guid userId)
    {
        private readonly List<AssistantSource> _sources = [];

        public Guid UserId { get; } = userId;
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
