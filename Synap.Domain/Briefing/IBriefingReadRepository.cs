namespace Synap.Domain;

/// <summary>
/// The three questions a briefing asks the database (daily-briefing design.md Decision 4). Reads
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

    /// <summary>
    /// The user's notes whose title or text carries one of the open-thread markers, newest first.
    /// <paramref name="stemmedMarkers"/> go through the indexed search vector;
    /// <paramref name="literalMarkers"/> are matched as substrings, for the ones Spanish text
    /// search discards as stopwords.
    /// </summary>
    Task<BriefingSection<BriefingNote>> ListOpenThreadNotesAsync(
        Guid userId,
        IReadOnlyList<string> stemmedMarkers,
        IReadOnlyList<string> literalMarkers,
        int limit,
        CancellationToken cancellationToken = default);
}
