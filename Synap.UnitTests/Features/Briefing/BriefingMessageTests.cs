using Synap.Application.Features.Briefing;
using Synap.Domain;

namespace Synap.UnitTests.Features.Briefing;

/// <summary>
/// daily-briefing tasks 3.1 to 3.3, revised by note-status task 6.5 - specs/briefing "What a
/// briefing contains". A pure function, so every promise is checked with a fixed clock, no Telegram
/// and no database.
/// </summary>
public class BriefingMessageTests
{
    private const string Madrid = "Europe/Madrid";

    /// <summary>09:30 in Madrid, in September (CEST, +02:00).</summary>
    private static readonly DateTime HalfPastNineInMadrid = new(2026, 9, 30, 7, 30, 0, DateTimeKind.Utc);

    private static BriefingReminder Reminder(string text, DateTime? dueAt = null, string? noteTitle = null)
        => new(Guid.NewGuid(), text, dueAt ?? HalfPastNineInMadrid, noteTitle);

    private static BriefingNote Note(string? title, string content = "contenido de la nota", int? pausedForDays = null)
        => new(Guid.NewGuid(), title, content, pausedForDays);

    private static BriefingEvent Event(
        string title, int startHourUtc = 7, int endHourUtc = 8, bool allDay = false, params BriefingNote[] related)
        => new(
            allDay
                ? CalendarEvent.AllDay(title, new DateOnly(2026, 9, 30))
                : new CalendarEvent(title, new DateTime(2026, 9, 30, startHourUtc, 30, 0, DateTimeKind.Utc),
                    new DateTime(2026, 9, 30, endHourUtc, 30, 0, DateTimeKind.Utc)),
            related);

    private static BriefingContent With(
        IReadOnlyList<BriefingEvent>? events = null,
        int? eventsTotal = null,
        IReadOnlyList<BriefingReminder>? reminders = null,
        IReadOnlyList<BriefingNote>? inProgress = null,
        IReadOnlyList<BriefingNote>? pending = null,
        IReadOnlyList<BriefingNote>? untagged = null,
        int? remindersTotal = null,
        int? inProgressTotal = null,
        int? pendingTotal = null,
        int? untaggedTotal = null,
        BriefingCalendar? calendar = null)
        => new(
            calendar ?? new BriefingCalendar.Events(new BriefingSection<BriefingEvent>(events ?? [], eventsTotal ?? events?.Count ?? 0)),
            new BriefingSection<BriefingReminder>(reminders ?? [], remindersTotal ?? reminders?.Count ?? 0),
            new BriefingSection<BriefingNote>(inProgress ?? [], inProgressTotal ?? inProgress?.Count ?? 0),
            new BriefingSection<BriefingNote>(pending ?? [], pendingTotal ?? pending?.Count ?? 0),
            new BriefingSection<BriefingNote>(untagged ?? [], untaggedTotal ?? untagged?.Count ?? 0));

    // ---- The day's events (calendar-integration task 5.4) ----

    /// <summary>specs/briefing "Today's events".</summary>
    [Fact]
    public void An_event_is_stated_with_its_title_and_local_time()
    {
        var text = BriefingMessage.Text(With(events: [Event("Demo con el cliente")]), Madrid)!;

        Assert.Contains("📅 <b>Hoy en tu calendario</b> (1)", text);
        Assert.Contains("09:30-10:30 · <b>Demo con el cliente</b>", text);
        // 07:30 is the UTC instant, which is not what the user reads.
        Assert.DoesNotContain("07:30", text);
    }

    /// <summary>
    /// design.md Decision 6 - first, because it is the only section tied to the clock.
    /// </summary>
    [Fact]
    public void The_events_come_before_the_reminders()
    {
        var text = BriefingMessage.Text(
            With(events: [Event("Demo con el cliente")], reminders: [Reminder("Llamar al banco")]), Madrid)!;

        Assert.True(
            text.IndexOf("📅 <b>Hoy en tu calendario</b>", StringComparison.Ordinal)
                < text.IndexOf("⏰ <b>Recordatorios de hoy</b>", StringComparison.Ordinal),
            text);
    }

    /// <summary>specs/calendar-integration "An all-day event".</summary>
    [Fact]
    public void An_all_day_event_says_so_rather_than_naming_a_time()
    {
        var text = BriefingMessage.Text(
            With(events: [Event("Festivo local", startHourUtc: 0, endHourUtc: 0, allDay: true)]), Madrid)!;

        Assert.Contains("todo el día · <b>Festivo local</b>", text);
        Assert.DoesNotContain("00:30", text);
    }

    /// <summary>specs/briefing "An event with related notes".</summary>
    [Fact]
    public void An_events_related_notes_hang_off_it()
    {
        var text = BriefingMessage.Text(
            With(events: [Event("Revisión OAuth", related: [Note("Migrar auth a OAuth"), Note(null, "apuntes del token")])]),
            Madrid)!;

        Assert.Contains("09:30-10:30 · <b>Revisión OAuth</b>", text);
        Assert.Contains("↳ Migrar auth a OAuth", text);
        // A note with no title is identified by a preview of its text, as everywhere else.
        Assert.Contains("↳ apuntes del token", text);
    }

    /// <summary>specs/briefing "An event with no related notes" - listed, on its own.</summary>
    [Fact]
    public void An_event_with_no_related_notes_is_listed_alone()
    {
        var text = BriefingMessage.Text(With(events: [Event("Dentista")]), Madrid)!;

        Assert.Contains("· <b>Dentista</b>", text);
        Assert.DoesNotContain("↳", text);
    }

    /// <summary>
    /// specs/briefing "No calendar connected" - when the day has no events (empty Events section)
    /// the section is omitted silently, just like every empty section.
    /// </summary>
    [Fact]
    public void Without_events_there_is_no_section_at_all()
    {
        var text = BriefingMessage.Text(With(reminders: [Reminder("Llamar al banco")]), Madrid)!;

        Assert.DoesNotContain("📅", text);
        Assert.DoesNotContain("Hoy</b>", text);
        Assert.Contains("Llamar al banco", text);
    }

    /// <summary>
    /// specs/briefing "The calendar could not be read" - an Unreadable calendar shows a notice
    /// line (not a full section) so the user knows there may be events they are not seeing.
    /// </summary>
    [Fact]
    public void An_unreadable_calendar_shows_a_notice_line()
    {
        var text = BriefingMessage.Text(
            With(reminders: [Reminder("Llamar al banco")],
                 calendar: new BriefingCalendar.Unreadable(NeedsReconnect: false)),
            Madrid)!;

        Assert.Contains("📅 No se ha podido leer el calendario.", text);
        Assert.DoesNotContain("<b>Hoy en tu calendario</b>", text);
    }

    /// <summary>
    /// specs/briefing "Reconnect required" - when NeedsReconnect is true, the notice asks to
    /// reconnect in Settings.
    /// </summary>
    [Fact]
    public void An_unreadable_calendar_that_needs_reconnect_says_so()
    {
        var text = BriefingMessage.Text(
            With(reminders: [Reminder("Llamar al banco")],
                 calendar: new BriefingCalendar.Unreadable(NeedsReconnect: true)),
            Madrid)!;

        Assert.Contains("Reconéctalo en Configuración.", text);
    }


    [Fact]
    public void A_capped_event_section_states_its_total()
    {
        var text = BriefingMessage.Text(
            With(events: [Event("Demo con el cliente"), Event("Retro")], eventsTotal: 7), Madrid)!;

        Assert.Contains("📅 <b>Hoy en tu calendario</b> (7)", text);
        Assert.Contains("… y 5 más", text);
    }

    [Fact]
    public void An_events_title_is_escaped_like_everything_else()
    {
        var text = BriefingMessage.Text(With(events: [Event("Diseño <b>&</b> arquitectura")]), Madrid)!;

        Assert.Contains("Diseño &lt;b&gt;&amp;&lt;/b&gt; arquitectura", text);
    }

    /// <summary>A day whose only entry is on the calendar is still a day with something to say.</summary>
    [Fact]
    public void A_day_with_only_events_is_still_a_briefing()
        => Assert.NotNull(BriefingMessage.Text(With(events: [Event("Demo con el cliente")]), Madrid));

    // ---- 3.1 What it says ----

    [Fact]
    public void A_reminder_is_stated_with_its_local_time()
    {
        var text = BriefingMessage.Text(With(reminders: [Reminder("Llamar al banco")]), Madrid)!;

        Assert.Contains("09:30 · Llamar al banco", text);
        // 07:30 is the UTC instant, which is not what the user reads.
        Assert.DoesNotContain("07:30", text);
    }

    [Fact]
    public void Without_a_timezone_a_reminder_is_stated_in_utc()
    {
        var text = BriefingMessage.Text(With(reminders: [Reminder("Llamar al banco")]), timezone: null)!;

        Assert.Contains("07:30 · Llamar al banco", text);
    }

    [Fact]
    public void A_reminder_about_a_note_names_it()
    {
        var text = BriefingMessage.Text(With(reminders: [Reminder("Renovar", noteTitle: "Certificados")]), Madrid)!;

        Assert.Contains("Renovar (Certificados)", text);
    }

    [Fact]
    public void A_note_is_identified_by_its_title()
    {
        var text = BriefingMessage.Text(With(untagged: [Note("Configurar pgvector")]), Madrid)!;

        Assert.Contains("Configurar pgvector", text);
    }

    /// <summary>The same rule an answer's sources use, so a note reads the same wherever it appears.</summary>
    [Fact]
    public void A_note_with_no_title_is_identified_by_a_preview_of_its_text()
    {
        var content = new string('a', NoteDisplay.PreviewChars + 20);

        var text = BriefingMessage.Text(With(untagged: [Note(null, content)]), Madrid)!;

        Assert.Contains(new string('a', NoteDisplay.PreviewChars) + "…", text);
    }

    [Fact]
    public void The_four_sections_appear_with_their_own_headings()
    {
        var text = BriefingMessage.Text(
            With(
                reminders: [Reminder("Banco")], inProgress: [Note("Migrar auth")],
                pending: [Note("Hablar con X")], untagged: [Note("Sin etiquetar")]),
            Madrid)!;

        Assert.Contains("Recordatorios de hoy", text);
        Assert.Contains("En desarrollo", text);
        Assert.Contains("Pendiente", text);
        Assert.Contains("Notas sin etiquetar", text);
        // The heuristic it replaced is gone for good (note-status design.md Decision 7).
        Assert.DoesNotContain("Hilos abiertos", text);
    }

    /// <summary>
    /// note-status design.md Decision 7 - in progress first: it is the shorter and more urgent
    /// section, and the one that says what is actually at hand.
    /// </summary>
    [Fact]
    public void In_progress_comes_before_pending()
    {
        var text = BriefingMessage.Text(
            With(inProgress: [Note("Migrar auth")], pending: [Note("Hablar con X")]), Madrid)!;

        Assert.True(text.IndexOf("En desarrollo", StringComparison.Ordinal)
            < text.IndexOf("Pendiente", StringComparison.Ordinal));
    }

    /// <summary>specs/briefing "A long-paused note resurfaces".</summary>
    [Fact]
    public void A_resurfaced_note_says_how_long_it_has_been_paused()
    {
        var text = BriefingMessage.Text(
            With(pending: [Note("Hablar con X", pausedForDays: 34)]), Madrid)!;

        Assert.Contains("Hablar con X", text);
        Assert.Contains("pausada hace 34 días", text);
    }

    [Fact]
    public void A_note_paused_for_a_single_day_is_said_in_the_singular()
    {
        var text = BriefingMessage.Text(With(pending: [Note("Hablar con X", pausedForDays: 1)]), Madrid)!;

        Assert.Contains("pausada hace 1 día", text);
        Assert.DoesNotContain("1 días", text);
    }

    [Fact]
    public void An_ordinary_pending_note_says_nothing_about_pausing()
    {
        var text = BriefingMessage.Text(With(pending: [Note("Hablar con X")]), Madrid)!;

        Assert.DoesNotContain("pausada", text);
    }

    [Fact]
    public void Markup_characters_in_the_users_own_text_are_escaped()
    {
        var text = BriefingMessage.Text(With(untagged: [Note("Comparar <a> & <b>")]), Madrid)!;

        Assert.Contains("Comparar &lt;a&gt; &amp; &lt;b&gt;", text);
        Assert.DoesNotContain("<a>", text);
    }

    // ---- 3.2 Empty sections, and an empty briefing ----

    /// <summary>specs/briefing "An empty section is left out".</summary>
    [Fact]
    public void An_empty_section_leaves_no_heading_behind()
    {
        var text = BriefingMessage.Text(With(untagged: [Note("Sin etiquetar")]), Madrid)!;

        Assert.Contains("Notas sin etiquetar", text);
        Assert.DoesNotContain("Recordatorios de hoy", text);
        Assert.DoesNotContain("En desarrollo", text);
        Assert.DoesNotContain("Pendiente", text);
    }

    /// <summary>
    /// note-status task 6.5 - the day this ships nobody has marked anything, so both status
    /// sections are absent and the briefing is still a sensible message.
    /// </summary>
    [Fact]
    public void A_briefing_with_nothing_marked_shows_neither_status_section()
    {
        var text = BriefingMessage.Text(With(reminders: [Reminder("Banco")]), Madrid)!;

        Assert.Contains("Recordatorios de hoy", text);
        Assert.DoesNotContain("En desarrollo", text);
        Assert.DoesNotContain("Pendiente", text);
    }

    /// <summary>specs/briefing "Empty day" - not an empty message, no message at all.</summary>
    [Fact]
    public void Nothing_to_report_is_not_a_message()
    {
        Assert.Null(BriefingMessage.Text(BriefingContent.Empty, Madrid));
    }

    /// <summary>
    /// The on-demand path needs something to say on that same day, because a request that produces
    /// silence looks broken (specs/briefing "Asked for with nothing to report").
    /// </summary>
    [Fact]
    public void There_is_a_fixed_text_for_a_day_with_nothing_to_report()
    {
        Assert.Contains("nada pendiente", BriefingMessage.NothingToReport);
        // It must not keep promising the section that no longer exists.
        Assert.DoesNotContain("hilos abiertos", BriefingMessage.NothingToReport);
    }

    // ---- 3.3 Counts ----

    /// <summary>specs/briefing "More items than are shown".</summary>
    [Fact]
    public void A_truncated_section_says_how_many_there_are_in_all()
    {
        var shown = Enumerable.Range(0, 5).Select(i => Note($"Nota {i}")).ToList();

        var text = BriefingMessage.Text(With(untagged: shown, untaggedTotal: 12), Madrid)!;

        Assert.Contains("(12)", text);
        Assert.Contains("y 7 más", text);
    }

    [Fact]
    public void A_section_that_shows_everything_says_no_more()
    {
        var text = BriefingMessage.Text(With(untagged: [Note("Una"), Note("Dos")]), Madrid)!;

        Assert.Contains("(2)", text);
        Assert.DoesNotContain("más", text);
    }

    [Fact]
    public void Every_section_carries_its_own_count()
    {
        var text = BriefingMessage.Text(
            With(
                reminders: [Reminder("Banco")], remindersTotal: 3,
                inProgress: [Note("Migrar auth")], inProgressTotal: 2,
                pending: [Note("Hablar con X")], pendingTotal: 8,
                untagged: [Note("Sin etiquetar")], untaggedTotal: 1),
            Madrid)!;

        Assert.Contains("Recordatorios de hoy</b> (3)", text);
        Assert.Contains("En desarrollo</b> (2)", text);
        Assert.Contains("Pendiente</b> (8)", text);
        Assert.Contains("Notas sin etiquetar</b> (1)", text);
    }

    // ── smart-resurfacing 1.10 — sección «Para retomar» ─────────────────────────────────────

    private static BriefingResurfacedNote Resurfaced(string? title, BriefingNote reason, int dormantForDays = 40)
        => new(Guid.NewGuid(), title, "contenido de la nota", dormantForDays, reason);

    private static BriefingContent WithResurfaced(IReadOnlyList<BriefingResurfacedNote> resurfaced)
        => new(BriefingCalendar.Default, BriefingSection<BriefingReminder>.Empty,
            BriefingSection<BriefingNote>.Empty, BriefingSection<BriefingNote>.Empty,
            BriefingSection<BriefingNote>.Empty, resurfaced);

    [Fact]
    public void Resurfaced_notes_section_appears_with_days_and_reason()
    {
        var reason = Note("Helm upgrade");
        var note = Resurfaced("Kubernetes deploy", reason, 42);
        var text = BriefingMessage.Text(WithResurfaced([note]), null)!;

        Assert.Contains("Para retomar</b> (1)", text);
        Assert.Contains("Kubernetes deploy", text);
        Assert.Contains("42 días sin actividad", text);
        Assert.Contains("Helm upgrade", text);
    }

    [Fact]
    public void Resurfaced_section_goes_last()
    {
        var reason = Note("Reciente");
        var content = new BriefingContent(
            BriefingCalendar.Default,
            BriefingSection<BriefingReminder>.Empty,
            BriefingSection<BriefingNote>.Empty,
            new BriefingSection<BriefingNote>([Note("Pendiente")], 1),
            BriefingSection<BriefingNote>.Empty,
            [Resurfaced("Dormida", reason)]);

        var text = BriefingMessage.Text(content, null)!;

        var pendingPos = text.IndexOf("Pendiente</b>", StringComparison.Ordinal);
        var resurfacedPos = text.IndexOf("Para retomar</b>", StringComparison.Ordinal);
        Assert.True(pendingPos < resurfacedPos, "«Para retomar» debe ir después de «Pendiente»");
    }

    [Fact]
    public void Resurfaced_section_absent_when_empty()
    {
        var text = BriefingMessage.Text(With(pending: [Note("Una nota")]), null)!;

        Assert.DoesNotContain("Para retomar", text);
    }

    [Fact]
    public void Resurfaced_title_with_html_is_escaped()
    {
        var reason = Note("Reason");
        var note = Resurfaced("<bold>nota</bold>", reason, 5);
        var text = BriefingMessage.Text(WithResurfaced([note]), null)!;

        Assert.DoesNotContain("<bold>", text);
        Assert.Contains("&lt;bold&gt;", text);
    }
}
