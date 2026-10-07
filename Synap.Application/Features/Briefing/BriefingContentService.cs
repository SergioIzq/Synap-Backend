using Synap.Domain;

namespace Synap.Application.Features.Briefing;

/// <summary>
/// Builds one user's briefing content out of the four queries (daily-briefing design.md
/// Decision 4, extended by note-status Decision 7). The local day is resolved here rather than in the repository, because only this
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
    /// What the user has waiting on the local day <paramref name="nowUtc"/> falls in. The four
    /// queries are independent, so nothing here depends on the order they run in.
    /// </summary>
    public async Task<BriefingContent> BuildAsync(
        Guid userId, DateTime nowUtc, string? timezone, CancellationToken cancellationToken = default)
    {
        var (dayStartUtc, dayEndUtc) = LocalDay(nowUtc, timezone);

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

        return new BriefingContent(reminders, inProgress, pending, untagged);
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
