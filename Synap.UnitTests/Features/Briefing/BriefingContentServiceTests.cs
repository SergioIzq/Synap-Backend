using Synap.Application.Features.Briefing;
using Synap.Domain;

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

    private BriefingContentService Service() => new(_repository);

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
    }
}
