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
    ];

    private static AgentTool Tool(string name, string description, string parameters)
        => new(name, description, JsonDocument.Parse(parameters).RootElement.Clone());
}
