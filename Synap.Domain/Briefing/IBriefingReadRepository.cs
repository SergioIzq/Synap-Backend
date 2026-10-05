namespace Synap.Domain;

/// <summary>
/// The four questions a briefing asks the database (daily-briefing design.md Decision 4, extended
/// by note-status Decision 7, which replaced the text-marker question with two status ones). Reads
/// only, and always scoped to one user: a briefing carries that user's own notes and reminders and
/// nothing else (specs/briefing "A briefing never crosses users").
///
/// Each returns the items to show plus the total there are, so a capped section can still say how
/// many it stands for without a second round trip.
/// </summary>
public interface IBriefingReadRepository
{
    /// <summary>
    /// The user's pending reminders due inside the local day, given as the UTC instants that day
    /// begins and ends at - the caller resolves those with <see cref="UserClock"/>, because only
    /// it knows the user's timezone and the days that are not 24 hours long.
    /// </summary>
    Task<BriefingSection<BriefingReminder>> ListRemindersDueAsync(
        Guid userId, DateTime fromUtc, DateTime toUtcExclusive, int limit, CancellationToken cancellationToken = default);

    /// <summary>The user's notes created since <paramref name="sinceUtc"/> that carry no tag, newest first.</summary>
    Task<BriefingSection<BriefingNote>> ListUntaggedNotesAsync(
        Guid userId, DateTime sinceUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>The user's notes marked as in progress, newest first.</summary>
    Task<BriefingSection<BriefingNote>> ListInProgressNotesAsync(
        Guid userId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's notes marked as pending, newest first, together with those that have stood
    /// paused since before <paramref name="pausedBeforeUtc"/> - a note paused that long rejoins the
    /// pending section carrying how many days it has been paused (specs/briefing "A long-paused
    /// note resurfaces"). A recently paused note is absent, which is what pausing is for.
    /// </summary>
    Task<BriefingSection<BriefingNote>> ListPendingNotesAsync(
        Guid userId, DateTime pausedBeforeUtc, DateTime nowUtc, int limit, CancellationToken cancellationToken = default);
}
