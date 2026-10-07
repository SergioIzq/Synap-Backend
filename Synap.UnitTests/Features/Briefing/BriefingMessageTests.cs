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

    private static BriefingContent With(
        IReadOnlyList<BriefingReminder>? reminders = null,
        IReadOnlyList<BriefingNote>? inProgress = null,
        IReadOnlyList<BriefingNote>? pending = null,
        IReadOnlyList<BriefingNote>? untagged = null,
        int? remindersTotal = null,
        int? inProgressTotal = null,
        int? pendingTotal = null,
        int? untaggedTotal = null)
        => new(
            new BriefingSection<BriefingReminder>(reminders ?? [], remindersTotal ?? reminders?.Count ?? 0),
            new BriefingSection<BriefingNote>(inProgress ?? [], inProgressTotal ?? inProgress?.Count ?? 0),
            new BriefingSection<BriefingNote>(pending ?? [], pendingTotal ?? pending?.Count ?? 0),
            new BriefingSection<BriefingNote>(untagged ?? [], untaggedTotal ?? untagged?.Count ?? 0));

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
}
