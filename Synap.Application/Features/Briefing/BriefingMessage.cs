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
    /// The calendar block: the events section and, when the copy could not be brought up to date,
    /// a single notice line (specs/briefing "Un calendario ilegible no detiene el briefing").
    /// <see cref="BriefingCalendar.NotConnected"/> → silent. A day with no events and no notice → silent.
    /// When the day has events of both scopes they are grouped into Trabajo and Personal
    /// (specs/briefing "Eventos de trabajo y personales").
    /// </summary>
    private static void CalendarSection(StringBuilder text, BriefingCalendar calendar, string? timezone)
    {
        if (calendar is not BriefingCalendar.Events(var section, var notice))
        {
            return;
        }

        var items = section.Items;
        if (items.Any(e => e.Event.Scope == CalendarScope.Work) && items.Any(e => e.Event.Scope == CalendarScope.Personal))
        {
            text.Append($"\n\n📅 <b>Hoy en tu calendario</b> ({section.Total})");
            foreach (var (scope, heading) in new[] { (CalendarScope.Work, "Trabajo"), (CalendarScope.Personal, "Personal") })
            {
                text.Append($"\n<i>{heading}</i>");
                foreach (var item in items.Where(e => e.Event.Scope == scope))
                {
                    text.Append($"\n· {EventLine(item, timezone)}");
                }
            }

            if (section.IsTruncated)
            {
                text.Append($"\n<i>… y {section.Total - items.Count} más</i>");
            }
        }
        else
        {
            Section(text, "📅", "Hoy en tu calendario", section, ev => EventLine(ev, timezone));
        }

        if (notice is not null)
        {
            text.Append($"\n\n📅 {Escape(NoticeLine(notice, timezone))}");
        }
    }

    /// <summary>
    /// "El calendario no se sincroniza desde las 07:40." - with the day too when it was not today,
    /// and the reminder to reconnect when that is what it takes.
    /// </summary>
    internal static string NoticeLine(BriefingCalendarNotice notice, string? timezone)
    {
        var since = notice.LastSyncedAt switch
        {
            null => "No se ha podido sincronizar el calendario.",
            { } at when UserClock.ToLocal(at, timezone).Date == UserClock.ToLocal(DateTime.UtcNow, timezone).Date =>
                $"El calendario no se sincroniza desde las {Local(at, timezone)}.",
            { } at => $"El calendario no se sincroniza desde el {UserClock.ToLocal(at, timezone).ToString("d 'de' MMMM 'a las' HH:mm", Spanish)}.",
        };

        return notice.NeedsReconnect ? $"{since} Reconéctalo en Configuración." : since;
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
