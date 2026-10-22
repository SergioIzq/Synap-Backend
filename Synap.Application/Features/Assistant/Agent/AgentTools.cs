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
    public const string GetCalendarEvents = "get_calendar_events";

    public const int MaxSearchLimit = 8;

    /// <summary>Eventos que se leen del proveedor en una consulta, antes de filtrar por `query`.</summary>
    public const int MaxCalendarEventsRead = 100;

    /// <summary>Eventos que se devuelven al modelo en una consulta.</summary>
    public const int MaxCalendarEventsShown = 25;

    /// <summary>La descripción de un evento se recorta: la escribe quien manda la invitación (design.md Decisión 10).</summary>
    public const int MaxEventDescriptionChars = 500;

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
        // "when" takes the user's own words instead of a computed instant: resolving the moment
        // was the only arithmetic any tool asked of the model, and the only tool it would not
        // call (observable-failures design.md Decision 5). Synap resolves it - see ReminderWording.
        Tool(SetReminder,
            "Remind the user about something, through Telegram: at a moment they name, or some time before an event of "
            + "their calendar. Only when they ask to be reminded or warned about something - never on your own initiative. "
            + "For a reminder relative to an event, first find it with get_calendar_events and pass its id as event_id.",
            """
            {"type": "object", "additionalProperties": false, "required": ["text"], "properties": {
              "text": {"type": "string", "maxLength": 500, "description": "What to remind them of, short, in their own words."},
              "when": {"type": "string", "description": "Required unless \"event\" is given. In the user's own words and nothing else. Without event_id, the moment: \"hoy a las 20:20\", \"el viernes\", \"mañana a las 8\", \"en dos semanas\". With event_id, how long before the event: \"una hora antes\", \"media hora antes\", \"el día antes\". Do not work out a date, an hour or a timezone - copy what they said."},
              "event_id": {"type": "string", "description": "The id (e1, e2...) of an event get_calendar_events returned in this conversation turn, when the reminder is relative to it."},
              "note_id": {"type": "string", "description": "A note of theirs the reminder is about, by the id a search returned. Only when the reminder really is about that note."},
              "recurrence": {"type": "string", "description": "For a repeating reminder: \"daily\", \"weekly:<0-6>\" (0 = Monday) or \"monthly:<1-28>\". Leave out for a one-off, and always with event_id."}
            }}
            """),
        // El intervalo con las palabras del usuario, como el `when` de set_reminder: lo resuelve
        // CalendarRangeWording (calendar-integration design.md Decisión 6). Se ofrece siempre, haya
        // o no calendario, para que el modelo no responda inventando.
        Tool(GetCalendarEvents,
            "Read the events of the user's calendar in a period. Read-only: you cannot create, change or delete events. "
            + "Use it whenever the user asks what they have on, when something is, or before setting a reminder relative "
            + "to an event. Each event comes with a short id (e1, e2...) that set_reminder accepts as event_id.",
            """
            {"type": "object", "additionalProperties": false, "required": ["range"], "properties": {
              "range": {"type": "string", "description": "The period in the user's own words and nothing else: \"hoy\", \"mañana\", \"el viernes\", \"esta semana\", \"la semana que viene\", \"este mes\", \"próximos 30 días\". Do not work out dates yourself. To find the next occurrence of something, use \"próximos 30 días\" with a query."},
              "query": {"type": "string", "description": "Optional words that must appear in the event title, e.g. \"sprint\" or \"dentista\"."}
            }}
            """),
    ];

    private static AgentTool Tool(string name, string description, string parameters)
        => new(name, description, JsonDocument.Parse(parameters).RootElement.Clone());
}
