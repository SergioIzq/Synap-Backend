using Synap.Domain;
using System.Text.Json;

namespace Synap.Application.Features.Assistant.Agent;

/// <summary>
/// The tools offered to the model (assistant-agent-foundations design.md Decision 2). There are
/// deliberately no destructive ones: nothing here deletes or overwrites (specs/ai-assistant
/// "Assistant actions are non-destructive").
/// </summary>
internal static class AgentTools
{
    public const string SearchNotes = "search_notes";
    public const string ReadNote = "read_note";
    public const string CreateNote = "create_note";
    public const string AddTags = "add_tags";
    public const string Remember = "remember";
    public const string SetReminder = "set_reminder";

    public const int MaxSearchLimit = 8;

    public static readonly IReadOnlyList<AgentTool> All =
    [
        Tool(SearchNotes,
            "Search the user's notes by meaning and by exact words. Returns short snippets; use read_note to see a whole note.",
            """
            {"type": "object", "additionalProperties": false, "required": ["query"], "properties": {
              "query": {"type": "string", "description": "What to look for, in the user's language."},
              "limit": {"type": "integer", "minimum": 1, "maximum": 8, "description": "How many notes to return (default 5)."}
            }}
            """),
        Tool(ReadNote,
            "Read one of the user's notes in full, by the id a search returned.",
            """
            {"type": "object", "additionalProperties": false, "required": ["note_id"], "properties": {
              "note_id": {"type": "string"}
            }}
            """),
        Tool(CreateNote,
            "Create a new note for the user. Only when they ask you to write something down.",
            """
            {"type": "object", "additionalProperties": false, "required": ["title", "content"], "properties": {
              "title": {"type": "string"},
              "content": {"type": "string"},
              "type": {"type": "string", "enum": ["Text", "Code"], "description": "Code for a code snippet; Text otherwise (default)."},
              "tags": {"type": "array", "items": {"type": "string"}, "maxItems": 10, "description": "Tag names without #."}
            }}
            """),
        Tool(AddTags,
            "Add tags to one of the user's existing notes. Only when they ask for it.",
            """
            {"type": "object", "additionalProperties": false, "required": ["note_id", "tags"], "properties": {
              "note_id": {"type": "string"},
              "tags": {"type": "array", "items": {"type": "string"}, "minItems": 1, "maxItems": 10, "description": "Tag names without #."}
            }}
            """),
        Tool(Remember,
            "Save a fact about the user to their memory. Only when they explicitly ask you to remember something.",
            """
            {"type": "object", "additionalProperties": false, "required": ["text"], "properties": {
              "text": {"type": "string", "maxLength": 200, "description": "The fact, short, in the user's words."}
            }}
            """),
        Tool(SetReminder,
            "Warn the user about something at a given moment, through Telegram. Only when they ask to be reminded "
            + "or warned about something - never on your own initiative.",
            """
            {"type": "object", "additionalProperties": false, "required": ["text", "due_at"], "properties": {
              "text": {"type": "string", "maxLength": 500, "description": "What to remind them of, short, in their own words."},
              "due_at": {"type": "string", "description": "When, as ISO 8601 in UTC (e.g. 2026-10-03T07:00:00Z). Resolve relative wording against the current moment and timezone given to you."},
              "note_id": {"type": "string", "description": "A note of theirs the reminder is about, by the id a search returned. Only when the reminder really is about that note."},
              "recurrence": {"type": "string", "description": "For a repeating reminder: \"daily\", \"weekly:<0-6>\" (0 = Monday) or \"monthly:<1-28>\". Leave out for a one-off."}
            }}
            """),
    ];

    private static AgentTool Tool(string name, string description, string parameters)
        => new(name, description, JsonDocument.Parse(parameters).RootElement.Clone());
}
