using Synap.Domain;

namespace Synap.Application.Features.Briefing;

/// <summary>
/// Builds one user's briefing content out of the three queries (daily-briefing design.md
/// Decision 4). The local day is resolved here rather than in the repository, because only this
/// layer knows the user's timezone - and <see cref="UserClock"/> is what already handles the days
/// that are not twenty-four hours long.
/// </summary>
public sealed class BriefingContentService
{
    private readonly IBriefingReadRepository _briefingReadRepository;

    public BriefingContentService(IBriefingReadRepository briefingReadRepository)
    {
        _briefingReadRepository = briefingReadRepository;
    }

    /// <summary>
    /// What the user has waiting on the local day <paramref name="nowUtc"/> falls in. The three
    /// queries are independent, so nothing here depends on the order they run in.
    /// </summary>
    public async Task<BriefingContent> BuildAsync(
        Guid userId, DateTime nowUtc, string? timezone, CancellationToken cancellationToken = default)
    {
        var (dayStartUtc, dayEndUtc) = LocalDay(nowUtc, timezone);

        var reminders = await _briefingReadRepository.ListRemindersDueAsync(
            userId, dayStartUtc, dayEndUtc, BriefingLimits.ItemsPerSection, cancellationToken);

        var untagged = await _briefingReadRepository.ListUntaggedNotesAsync(
            userId, nowUtc - BriefingLimits.UntaggedWindow, BriefingLimits.ItemsPerSection, cancellationToken);

        var openThreads = await _briefingReadRepository.ListOpenThreadNotesAsync(
            userId, BriefingLimits.StemmedMarkers, BriefingLimits.LiteralMarkers, BriefingLimits.ItemsPerSection, cancellationToken);

        return new BriefingContent(reminders, untagged, openThreads);
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
