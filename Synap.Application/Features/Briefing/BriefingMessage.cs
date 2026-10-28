using Synap.Domain;
using System.Globalization;
using System.Text;

namespace Synap.Application.Features.Briefing;

/// <summary>
/// Turns a <see cref="BriefingContent"/> into the message the user reads (daily-briefing design.md
/// Decision 5). Pure: no clock of its own, no Telegram, no database - every promise the spec makes
/// about what a briefing says is testable here.
///
/// HTML like the reminder's, because the sender posts with parse_mode=HTML.
/// </summary>
public static class BriefingMessage
{
    /// <summary>What a briefing the user asked for says when there is nothing to report.</summary>
    public const string NothingToReport = "☀️ Hoy no tienes nada pendiente: ni recordatorios, ni notas en curso o pendientes, ni notas sin etiquetar.";

    private static readonly CultureInfo Spanish = new("es-ES");

    /// <summary>
    /// The briefing's text, or null when there is nothing to report. Null is a distinct outcome
    /// rather than an empty string on purpose: the automatic briefing stays silent on such a day,
    /// and only the caller knows whether the user asked for this one (specs/briefing "Nothing to
    /// report means nothing is sent unasked").
    /// </summary>
    public static string? Text(BriefingContent content, string? timezone)
    {
        if (content.IsEmpty)
        {
            return null;
        }

        var text = new StringBuilder("☀️ <b>Buenos días.</b> Esto es lo que tienes hoy:");

        // First, and the only section tied to the clock (calendar-integration design.md Decision 6).
        // When nothing is connected: silent. When connected but unreadable: a one-line notice so the
        // user knows there may be events they are not seeing (specs/briefing "The calendar could not
        // be read"). The failure itself is recorded by the reader.
        CalendarSection(text, content.Calendar, timezone);

        Section(text, "⏰", "Recordatorios de hoy", content.RemindersToday, r =>
        {
            var at = Local(r.DueAt, timezone);
            // Parentheses, not another "·": the line already uses it as a bullet and as the
            // separator after the time, and a third meaning makes it unreadable.
            var note = string.IsNullOrWhiteSpace(r.NoteTitle) ? string.Empty : $" ({Escape(r.NoteTitle)})";
            return $"{at} · {Escape(r.Text)}{note}";
        });

        // In progress before pending, and both before the untagged notes: the shortest and most
        // urgent section first, so the three things actually at hand are not read past
        // (note-status design.md Decision 7).
        Section(text, "🔨", "En desarrollo", content.InProgress, NoteLine);
        Section(text, "📋", "Pendiente", content.Pending, NoteLine);
        Section(text, "📌", "Notas sin etiquetar", content.UntaggedNotes, NoteLine);

        // La sección de rescate va siempre al final (smart-resurfacing, Decisión 7).
        ResurfacedSection(text, content.ResurfacedNotes);

        return text.ToString();
    }

    private static void ResurfacedSection(StringBuilder text, IReadOnlyList<BriefingResurfacedNote> notes)
    {
        if (notes.Count == 0)
        {
            return;
        }

        text.Append($"\n\n🔁 <b>Para retomar</b> ({notes.Count})");
        foreach (var note in notes)
        {
            var label = Escape(NoteDisplay.Label(note.Title, note.Content));
            var days = note.DormantForDays;
            var sinceText = days == 1 ? "1 día sin actividad" : $"{days} días sin actividad";
            var reasonLabel = Escape(NoteDisplay.Label(note.Reason.Title, note.Reason.Content));
            text.Append($"\n· {label} <i>({sinceText}, relacionada con {reasonLabel})</i>");
        }
    }

    /// <summary>
    /// The calendar block: events section, one-line warning, or nothing.
    /// <see cref="BriefingCalendar.NotConnected"/> → silent.
    /// <see cref="BriefingCalendar.Events"/> (non-empty) → full section.
    /// <see cref="BriefingCalendar.Unreadable"/> → a single notice line.
    /// An empty <see cref="BriefingCalendar.Events"/> → silent (the day had no events).
    /// </summary>
    private static void CalendarSection(StringBuilder text, BriefingCalendar calendar, string? timezone)
    {
        switch (calendar)
        {
            case BriefingCalendar.Events e:
                Section(text, "📅", "Hoy en tu calendario", e.Section, ev => EventLine(ev, timezone));
                break;
            case BriefingCalendar.Unreadable u:
                var notice = u.NeedsReconnect
                    ? "No se ha podido leer el calendario. Reconéctalo en Configuración."
                    : "No se ha podido leer el calendario.";
                text.Append($"\n\n📅 {Escape(notice)}");
                break;
        }
    }

    /// <summary>
    /// An event's line: its title and the local time it runs, with the notes that relate to it
    /// underneath, indented so they read as belonging to it rather than as events of their own.
    /// An event lasting the whole day says so instead of naming a time it does not start at
    /// (specs/calendar-integration "An all-day event").
    /// </summary>
    private static string EventLine(BriefingEvent entry, string? timezone)
    {
        var when = entry.Event.IsAllDay
            ? "todo el día"
            : $"{Local(entry.Event.StartUtc, timezone)}-{Local(entry.Event.EndUtc, timezone)}";

        var line = new StringBuilder($"{when} · <b>{Escape(entry.Event.Title)}</b>");
        foreach (var note in entry.RelatedNotes)
        {
            line.Append($"\n   ↳ {NoteLine(note)}");
        }

        return line.ToString();
    }

    private static string Local(DateTime utc, string? timezone)
        => UserClock.ToLocal(utc, timezone).ToString("HH:mm", Spanish);

    /// <summary>
    /// A note's line. A note that rejoined the pending section after standing paused too long says
    /// how long it has been paused, so the user can tell it apart from something they queued today
    /// (specs/briefing "A long-paused note resurfaces").
    /// </summary>
    private static string NoteLine(BriefingNote note)
    {
        var label = Escape(NoteDisplay.Label(note.Title, note.Content));

        return note.PausedForDays is { } days
            ? $"{label} <i>⏸️ pausada hace {days} {(days == 1 ? "día" : "días")}</i>"
            : label;
    }

    /// <summary>
    /// One section, or nothing at all when it is empty - an empty heading is noise the user has to
    /// read past every morning (specs/briefing "An empty section is left out"). A capped section
    /// says how many it stands for, so the number is never silently lost.
    /// </summary>
    private static void Section<T>(StringBuilder text, string icon, string heading, BriefingSection<T> section, Func<T, string> line)
    {
        if (section.IsEmpty)
        {
            return;
        }

        text.Append($"\n\n{icon} <b>{heading}</b> ({section.Total})");
        foreach (var item in section.Items)
        {
            text.Append($"\n· {line(item)}");
        }

        if (section.IsTruncated)
        {
            var rest = section.Total - section.Items.Count;
            text.Append($"\n<i>… y {rest} más</i>");
        }
    }

    /// <summary>The three characters Telegram's HTML mode treats as markup.</summary>
    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
