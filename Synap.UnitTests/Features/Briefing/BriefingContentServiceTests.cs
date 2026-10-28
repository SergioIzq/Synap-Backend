using Synap.Application.Features.Briefing;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Features.Briefing;

/// <summary>
/// daily-briefing task 2.4 - assembling the three sections, and the local-day window the
/// reminders query is asked for. The queries themselves are covered against a real Postgres in
/// Synap.IntegrationTests/BriefingQueriesTests.
/// </summary>
public class BriefingContentServiceTests
{
    private const string Madrid = "Europe/Madrid";
    private static readonly Guid Me = Guid.NewGuid();

    private readonly FakeBriefingReadRepository _repository = new();
    private readonly FakeCalendarReader _calendar = new();
    private readonly FakeAiServiceClient _ai = new();

    private BriefingContentService Service() => new(_repository, _calendar, _ai);

    // ---- The local day ----

    [Fact]
    public async Task The_reminder_window_is_the_users_local_day_not_the_utc_one()
    {
        // 23:30 UTC on the 30th is already 01:30 on the 1st in Madrid (CEST, +02:00).
        var nowUtc = new DateTime(2026, 9, 30, 23, 30, 0, DateTimeKind.Utc);

        await Service().BuildAsync(Me, nowUtc, Madrid);

        // The day that contains that instant locally runs 1 Oct 00:00 -> 2 Oct 00:00 in Madrid.
        Assert.Equal(new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc), _repository.ReminderWindow.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc), _repository.ReminderWindow.ToUtc);
    }

    [Fact]
    public async Task Without_a_timezone_the_day_is_the_utc_one()
    {
        var nowUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        await Service().BuildAsync(Me, nowUtc, timezone: null);

        Assert.Equal(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), _repository.ReminderWindow.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), _repository.ReminderWindow.ToUtc);
    }

    /// <summary>
    /// The autumn clock change makes that local day twenty-five hours long. Taking the window from
    /// the local date rather than adding twenty-four hours is what keeps it a whole day.
    /// </summary>
    [Fact]
    public async Task A_day_with_a_clock_change_still_runs_midnight_to_midnight()
    {
        // 25 Oct 2026: Madrid goes 03:00 -> 02:00, so the local day lasts 25 hours.
        var nowUtc = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

        await Service().BuildAsync(Me, nowUtc, Madrid);

        var (from, to) = _repository.ReminderWindow;
        Assert.Equal(TimeSpan.FromHours(25), to - from);
        Assert.Equal(new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc), from);
    }

    [Fact]
    public async Task The_untagged_window_is_asked_for_from_the_current_moment()
    {
        var nowUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        await Service().BuildAsync(Me, nowUtc, Madrid);

        Assert.Equal(nowUtc - BriefingLimits.UntaggedWindow, _repository.UntaggedSince);
    }

    // ---- Assembling ----

    [Fact]
    public async Task The_four_sections_are_reported_as_the_queries_returned_them()
    {
        _repository.Reminders = new BriefingSection<BriefingReminder>(
            [new BriefingReminder(Guid.NewGuid(), "Llamar al banco", DateTime.UtcNow, null)], 1);
        _repository.Untagged = new BriefingSection<BriefingNote>(
            [new BriefingNote(Guid.NewGuid(), "Sin etiquetar", "algo")], 4);
        _repository.InProgress = new BriefingSection<BriefingNote>(
            [new BriefingNote(Guid.NewGuid(), "Migrar auth", "algo")], 1);
        _repository.Pending = new BriefingSection<BriefingNote>([], 0);

        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.Equal("Llamar al banco", Assert.Single(content.RemindersToday.Items).Text);
        Assert.True(content.UntaggedNotes.IsTruncated);
        Assert.Equal(4, content.UntaggedNotes.Total);
        Assert.Equal("Migrar auth", Assert.Single(content.InProgress.Items).Title);
        Assert.True(content.Pending.IsEmpty);
        Assert.False(content.IsEmpty);
    }

    /// <summary>
    /// note-status task 6.4 - the day this ships, nobody has marked anything, and the briefing has
    /// to behave rather than break (design.md Migration Plan / Risks).
    /// </summary>
    [Fact]
    public async Task A_user_who_has_marked_nothing_gets_neither_status_section()
    {
        _repository.Untagged = new BriefingSection<BriefingNote>(
            [new BriefingNote(Guid.NewGuid(), "Sin etiquetar", "algo")], 1);

        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.True(content.InProgress.IsEmpty);
        Assert.True(content.Pending.IsEmpty);
        Assert.False(content.IsEmpty);
    }

    /// <summary>
    /// A pending-only day is still a day with something to report: IsEmpty has to count the new
    /// sections, or the automatic briefing would stay silent on it.
    /// </summary>
    [Fact]
    public async Task A_day_with_only_pending_notes_is_not_empty()
    {
        _repository.Pending = new BriefingSection<BriefingNote>(
            [new BriefingNote(Guid.NewGuid(), "Migrar auth", "algo")], 1);

        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.False(content.IsEmpty);
    }

    /// <summary>
    /// calendar-integration task 5.2 - a day whose only entry is on the calendar is still a day
    /// with something to report, so IsEmpty has to count the event section too.
    /// </summary>
    [Fact]
    public void A_content_holding_only_events_is_not_empty()
    {
        var demo = new BriefingEvent(
            new CalendarEvent("Demo con el cliente", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)),
            RelatedNotes: []);

        var content = BriefingContent.Empty with
        {
            Calendar = new BriefingCalendar.Events(new BriefingSection<BriefingEvent>([demo], 1)),
        };

        Assert.False(content.IsEmpty);
        // EventsToday is the computed view of the same events.
        Assert.Single(content.EventsToday.Items);
        // And the day it came from is still empty without it.
        Assert.True(BriefingContent.Empty.IsEmpty);
    }

    /// <summary>
    /// specs/briefing "A day whose only calendar entry is an unreadable notice is empty" -
    /// IsEmpty must not count the warning, only the events themselves.
    /// </summary>
    [Fact]
    public void A_content_with_only_an_unreadable_calendar_is_empty()
    {
        var content = BriefingContent.Empty with
        {
            Calendar = new BriefingCalendar.Unreadable(NeedsReconnect: false),
        };

        Assert.True(content.IsEmpty);
        // And with needs-reconnect it is still empty.
        Assert.True((BriefingContent.Empty with { Calendar = new BriefingCalendar.Unreadable(true) }).IsEmpty);
    }

    /// <summary>
    /// calendar-integration design.md Decision 9 - a calendar that takes more than 10 seconds
    /// yields Unreadable so it does not delay the rest of the briefing.
    /// </summary>
    [Fact]
    public async Task A_calendar_that_times_out_gives_unreadable()
    {
        _calendar.HangForever();
        var service = new BriefingContentService(_repository, _calendar, _ai, TimeSpan.FromMilliseconds(50));

        var content = await service.BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.IsType<BriefingCalendar.Unreadable>(content.Calendar);
        Assert.True(content.EventsToday.IsEmpty);
    }

    /// <summary>design.md Decision 6 - the cutoff the service hands the query.</summary>
    [Fact]
    public async Task The_paused_cutoff_is_the_resurface_window_before_now()
    {
        var nowUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

        await Service().BuildAsync(Me, nowUtc, Madrid);

        Assert.Equal(nowUtc - BriefingLimits.PausedResurfaceAfter, _repository.PausedBefore);
        Assert.Equal(nowUtc, _repository.PendingNowUtc);
    }

    [Fact]
    public async Task Nothing_in_any_section_is_an_empty_briefing()
    {
        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.True(content.IsEmpty);
        Assert.True(BriefingContent.Empty.IsEmpty);
    }

    [Fact]
    public async Task Every_query_is_asked_for_the_briefed_user_only()
    {
        await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.Equal([Me, Me, Me, Me], _repository.UserIds);
    }

    [Fact]
    public async Task Each_section_is_capped_at_the_same_limit()
    {
        await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.Equal(
            [BriefingLimits.ItemsPerSection, BriefingLimits.ItemsPerSection,
             BriefingLimits.ItemsPerSection, BriefingLimits.ItemsPerSection],
            _repository.Limits);
    }

    // ---- The day's events (calendar-integration task 5.3) ----

    private static CalendarEvent Event(string title, int hourUtc = 8, bool allDay = false)
        => allDay
            ? CalendarEvent.AllDay(title, new DateOnly(2026, 10, 1))
            : new(title, new DateTime(2026, 10, 1, hourUtc, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 1, hourUtc + 1, 0, 0, DateTimeKind.Utc));

    private static NoteSearchHit Hit(string title, double? similarity)
        => new(Guid.NewGuid(), title, "Text", [], $"contenido de {title}", similarity);

    /// <summary>The calendar is asked for the user's own local day, like the reminders are.</summary>
    [Fact]
    public async Task The_calendar_is_read_for_the_users_local_day()
    {
        var nowUtc = new DateTime(2026, 9, 30, 23, 30, 0, DateTimeKind.Utc);

        await Service().BuildAsync(Me, nowUtc, Madrid);

        Assert.Equal(
            [(Me, new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc))],
            _calendar.Reads);
    }

    /// <summary>specs/briefing "An event with related notes".</summary>
    [Fact]
    public async Task An_event_carries_the_notes_that_relate_to_it()
    {
        _calendar.Returns(Event("Revisión OAuth"));
        _ai.Search = (_, _, _) => [Hit("Migrar auth a OAuth", 0.81)];

        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        var shown = Assert.Single(content.EventsToday.Items);
        Assert.Equal("Revisión OAuth", shown.Event.Title);
        Assert.Equal(["Migrar auth a OAuth"], shown.RelatedNotes.Select(n => n.Title));
        // Searched with the event's title, for that user, through the search the assistant uses.
        Assert.Equal([(Me, "Revisión OAuth", BriefingLimits.RelatedNotesPerEvent)], _ai.SearchCalls);
    }

    /// <summary>specs/briefing "An event with no related notes" - it is shown, on its own.</summary>
    [Fact]
    public async Task An_event_with_nothing_related_is_still_shown_alone()
    {
        _calendar.Returns(Event("Dentista"));

        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        var shown = Assert.Single(content.EventsToday.Items);
        Assert.Equal("Dentista", shown.Event.Title);
        Assert.Empty(shown.RelatedNotes);
        Assert.False(content.IsEmpty);
    }

    /// <summary>
    /// design.md Decision 5 - the floor is what stops an event dragging in the vault's
    /// least-unrelated notes, which is the failure mode that makes the feature look broken.
    /// </summary>
    [Fact]
    public async Task A_note_below_the_similarity_floor_is_not_related()
    {
        _calendar.Returns(Event("Comida con Marta"));
        _ai.Search = (_, _, _) =>
        [
            Hit("Receta de lentejas", BriefingLimits.RelatedNoteSimilarityFloor - 0.01),
            Hit("Cumpleaños de Marta", BriefingLimits.RelatedNoteSimilarityFloor),
        ];

        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.Equal(["Cumpleaños de Marta"], Assert.Single(content.EventsToday.Items).RelatedNotes.Select(n => n.Title));
    }

    /// <summary>
    /// A note the search found by its exact words carries no similarity at all, and is kept on
    /// that account: that matching is half of why this search is the one used (design.md Decision 5).
    /// </summary>
    [Fact]
    public async Task A_note_found_by_its_words_is_related_without_a_score()
    {
        _calendar.Returns(Event("Revisión NU1605"));
        _ai.Search = (_, _, _) => [Hit("Arreglar NU1605 en el build", null)];

        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.Equal(["Arreglar NU1605 en el build"], Assert.Single(content.EventsToday.Items).RelatedNotes.Select(n => n.Title));
    }

    /// <summary>specs/briefing "No calendar connected" - no section, and nothing is searched.</summary>
    [Fact]
    public async Task A_user_with_no_calendar_gets_no_event_section()
    {
        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.True(content.EventsToday.IsEmpty);
        Assert.Empty(_ai.SearchCalls);
    }

    /// <summary>
    /// specs/briefing "The calendar could not be read" - the rest of the briefing is built and
    /// delivered, and the section is absent rather than saying the day is free.
    /// </summary>
    [Fact]
    public async Task A_calendar_that_could_not_be_read_leaves_the_rest_of_the_briefing_alone()
    {
        _calendar.Unavailable();
        _repository.Untagged = new BriefingSection<BriefingNote>(
            [new BriefingNote(Guid.NewGuid(), "Sin etiquetar", "algo")], 1);

        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.True(content.EventsToday.IsEmpty);
        Assert.False(content.IsEmpty);
        Assert.Equal(1, content.UntaggedNotes.Total);
        Assert.Empty(_ai.SearchCalls);
    }

    /// <summary>
    /// Capped like every other section, and capped before the notes are looked for: a day with
    /// many events states its total and searches at most as many times as it shows.
    /// </summary>
    [Fact]
    public async Task A_day_with_more_events_than_are_shown_states_its_total()
    {
        var many = Enumerable.Range(0, BriefingLimits.EventsPerBriefing + 3)
            .Select(i => Event($"Evento {i}", hourUtc: 6 + i))
            .ToArray();
        _calendar.Returns(many);

        var content = await Service().BuildAsync(Me, DateTime.UtcNow, Madrid);

        Assert.Equal(BriefingLimits.EventsPerBriefing, content.EventsToday.Items.Count);
        Assert.Equal(many.Length, content.EventsToday.Total);
        Assert.True(content.EventsToday.IsTruncated);
        Assert.Equal(BriefingLimits.EventsPerBriefing, _ai.SearchCalls.Count);
        // The earliest of the day, in order, whatever order the calendar answered in.
        Assert.Equal(["Evento 0", "Evento 1", "Evento 2", "Evento 3", "Evento 4"],
            content.EventsToday.Items.Select(e => e.Event.Title));
    }

    private sealed class FakeBriefingReadRepository : IBriefingReadRepository
    {
        public BriefingSection<BriefingReminder> Reminders { get; set; } = BriefingSection<BriefingReminder>.Empty;
        public BriefingSection<BriefingNote> Untagged { get; set; } = BriefingSection<BriefingNote>.Empty;
        public BriefingSection<BriefingNote> InProgress { get; set; } = BriefingSection<BriefingNote>.Empty;
        public BriefingSection<BriefingNote> Pending { get; set; } = BriefingSection<BriefingNote>.Empty;

        public (DateTime FromUtc, DateTime ToUtc) ReminderWindow { get; private set; }
        public DateTime UntaggedSince { get; private set; }
        public DateTime PausedBefore { get; private set; }
        public DateTime PendingNowUtc { get; private set; }
        public List<Guid> UserIds { get; } = [];
        public List<int> Limits { get; } = [];

        public Task<BriefingSection<BriefingReminder>> ListRemindersDueAsync(
            Guid userId, DateTime fromUtc, DateTime toUtcExclusive, int limit, CancellationToken cancellationToken = default)
        {
            ReminderWindow = (fromUtc, toUtcExclusive);
            UserIds.Add(userId);
            Limits.Add(limit);
            return Task.FromResult(Reminders);
        }

        public Task<BriefingSection<BriefingNote>> ListUntaggedNotesAsync(
            Guid userId, DateTime sinceUtc, int limit, CancellationToken cancellationToken = default)
        {
            UntaggedSince = sinceUtc;
            UserIds.Add(userId);
            Limits.Add(limit);
            return Task.FromResult(Untagged);
        }

        public Task<BriefingSection<BriefingNote>> ListInProgressNotesAsync(
            Guid userId, int limit, CancellationToken cancellationToken = default)
        {
            UserIds.Add(userId);
            Limits.Add(limit);
            return Task.FromResult(InProgress);
        }

        public Task<BriefingSection<BriefingNote>> ListPendingNotesAsync(
            Guid userId, DateTime pausedBeforeUtc, DateTime nowUtc, int limit, CancellationToken cancellationToken = default)
        {
            PausedBefore = pausedBeforeUtc;
            PendingNowUtc = nowUtc;
            UserIds.Add(userId);
            Limits.Add(limit);
            return Task.FromResult(Pending);
        }

        public Task<IReadOnlyList<Guid>> ListRecentAnchorIdsAsync(
            Guid userId, DateTime recentSinceUtc, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<Guid>> ListDormantCandidateIdsAsync(
            Guid userId, DateTime dormantBeforeUtc, DateTime cooldownBeforeUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<BriefingNoteDetail>> ListNoteDetailsByIdsAsync(
            Guid userId, IReadOnlyList<Guid> noteIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BriefingNoteDetail>>([]);
    }
}
