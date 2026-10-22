using Synap.Domain;
using Synap.Application.Features.Calendar;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Briefing;

/// <summary>
/// Builds one user's briefing content out of the four queries (daily-briefing design.md
/// Decision 4, extended by note-status Decision 7) and, since calendar-integration, the day's
/// events. The local day is resolved here rather than in the repository, because only this
/// layer knows the user's timezone - and <see cref="UserClock"/> is what already handles the days
/// that are not twenty-four hours long.
/// </summary>
public sealed class BriefingContentService
{
    private readonly IBriefingReadRepository _briefingReadRepository;
    private readonly ICalendarReader _calendarReader;
    private readonly IAiServiceClient _aiServiceClient;
    private readonly TimeSpan _calendarTimeout;

    public BriefingContentService(
        IBriefingReadRepository briefingReadRepository,
        ICalendarReader calendarReader,
        IAiServiceClient aiServiceClient,
        TimeSpan? calendarTimeout = null)
    {
        _briefingReadRepository = briefingReadRepository;
        _calendarReader = calendarReader;
        _aiServiceClient = aiServiceClient;
        _calendarTimeout = calendarTimeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// What the user has waiting on the local day <paramref name="nowUtc"/> falls in. The four
    /// queries are independent, so nothing here depends on the order they run in.
    /// </summary>
    public async Task<BriefingContent> BuildAsync(
        Guid userId, DateTime nowUtc, string? timezone, CancellationToken cancellationToken = default)
    {
        var (dayStartUtc, dayEndUtc) = LocalDay(nowUtc, timezone);

        var calendar = await EventsOfTheDayAsync(userId, dayStartUtc, dayEndUtc, cancellationToken);

        var reminders = await _briefingReadRepository.ListRemindersDueAsync(
            userId, dayStartUtc, dayEndUtc, BriefingLimits.ItemsPerSection, cancellationToken);

        var inProgress = await _briefingReadRepository.ListInProgressNotesAsync(
            userId, BriefingLimits.ItemsPerSection, cancellationToken);

        // The cutoff is computed here, not in the repository, for the same reason the local day is:
        // the policy of how long pausing lasts belongs to this layer (design.md Decision 6).
        var pending = await _briefingReadRepository.ListPendingNotesAsync(
            userId, nowUtc - BriefingLimits.PausedResurfaceAfter, nowUtc, BriefingLimits.ItemsPerSection, cancellationToken);

        var untagged = await _briefingReadRepository.ListUntaggedNotesAsync(
            userId, nowUtc - BriefingLimits.UntaggedWindow, BriefingLimits.ItemsPerSection, cancellationToken);

        return new BriefingContent(calendar, reminders, inProgress, pending, untagged);
    }

    /// <summary>
    /// The day's calendar state: <see cref="BriefingCalendar.NotConnected"/> when no calendar is
    /// configured, <see cref="BriefingCalendar.Unreadable"/> when it could not be read (or timed
    /// out), or <see cref="BriefingCalendar.Events"/> with the day's events.
    /// A 10-second ceiling prevents a slow CalDAV server from delaying the rest of the briefing
    /// (calendar-integration design.md Decision 9).
    /// </summary>
    private async Task<BriefingCalendar> EventsOfTheDayAsync(
        Guid userId, DateTime dayStartUtc, DateTime dayEndUtc, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_calendarTimeout);
        try
        {
            var read = await _calendarReader.ListEventsAsync(userId, dayStartUtc, dayEndUtc, 50, cts.Token);

            return read switch
            {
                CalendarReadResult.NotConnected => BriefingCalendar.Default,
                CalendarReadResult.NeedsReconnect => new BriefingCalendar.Unreadable(NeedsReconnect: true),
                CalendarReadResult.Unavailable => new BriefingCalendar.Unreadable(NeedsReconnect: false),
                CalendarReadResult.Ok(var all) when all.Count == 0 =>
                    new BriefingCalendar.Events(BriefingSection<BriefingEvent>.Empty),
                CalendarReadResult.Ok(var all) => await BuildEventsSectionAsync(userId, all, cts.Token),
                _ => BriefingCalendar.Default,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own 10-second ceiling fired, not an external cancellation.
            return new BriefingCalendar.Unreadable(NeedsReconnect: false);
        }
    }

    private async Task<BriefingCalendar> BuildEventsSectionAsync(
        Guid userId, IReadOnlyList<CalendarEvent> all, CancellationToken cancellationToken)
    {
        // Capped before the notes are looked for: a day with twenty events states its total and
        // searches five times, not twenty.
        // All-day events first (specs/briefing "All-day events come before timed events"), then by
        // start time so the day reads in order.
        var shown = all.OrderBy(e => e.IsAllDay ? 0 : 1).ThenBy(e => e.StartUtc)
            .Take(BriefingLimits.EventsPerBriefing).ToList();

        var events = new List<BriefingEvent>(shown.Count);
        foreach (var calendarEvent in shown)
        {
            events.Add(new BriefingEvent(
                calendarEvent, await RelatedNotesAsync(userId, calendarEvent.Title, cancellationToken)));
        }

        return new BriefingCalendar.Events(new BriefingSection<BriefingEvent>(events, all.Count));
    }

    /// <summary>
    /// The user's own notes that relate to an event, through the same search the assistant's
    /// `search_notes` uses - so "related" has one definition in Synap rather than two
    /// (design.md Decision 5). Kept only above <see cref="BriefingLimits.RelatedNoteSimilarityFloor"/>,
    /// which is why an event with nothing related is shown alone rather than with the vault's
    /// least-unrelated notes (specs/briefing "An event with no related notes").
    ///
    /// An unreachable AI service answers with nothing, which is the same as nothing related: the
    /// event is still shown, and the briefing is still delivered.
    /// </summary>
    private async Task<IReadOnlyList<BriefingNote>> RelatedNotesAsync(
        Guid userId, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var hits = await _aiServiceClient.SearchAsync(
            userId, title, BriefingLimits.RelatedNotesPerEvent,
            minSimilarity: BriefingLimits.RelatedNoteSimilarityFloor, cancellationToken);

        return hits
            // A note found by its exact words carries no similarity and is kept on that account.
            .Where(h => h.Similarity is null || h.Similarity >= BriefingLimits.RelatedNoteSimilarityFloor)
            .Select(h => new BriefingNote(h.Id, h.Title, h.Snippet))
            .ToList();
    }

    /// <summary>
    /// The UTC instants the user's local day begins and ends at, half-open so a reminder at
    /// midnight belongs to one day only. Taken from the local date rather than by adding 24 hours,
    /// so the days a clock change makes 23 or 25 hours long still start and end at midnight.
    /// </summary>
    internal static (DateTime StartUtc, DateTime EndUtcExclusive) LocalDay(DateTime nowUtc, string? timezone)
        => (UserClock.AtLocalTimeOfDay(nowUtc, timezone, days: 0, TimeSpan.Zero),
            UserClock.AtLocalTimeOfDay(nowUtc, timezone, days: 1, TimeSpan.Zero));
}
