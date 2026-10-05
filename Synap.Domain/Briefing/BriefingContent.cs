namespace Synap.Domain;

/// <summary>A reminder falling due on the local day being briefed; <see cref="DueAt"/> is UTC.</summary>
public sealed record BriefingReminder(Guid Id, string Text, DateTime DueAt, string? NoteTitle);

/// <summary>
/// A note the briefing points at. <see cref="Title"/> and <see cref="Content"/> are both carried
/// so the message can identify a note with no title by a preview of its text, the way an answer's
/// sources already do (specs/briefing "What a briefing contains").
///
/// <see cref="PausedForDays"/> is set only for a note that rejoined the pending section after
/// standing paused too long, and is what lets the message say how long it has been paused
/// (specs/briefing "A long-paused note resurfaces"). Null for every other note.
/// </summary>
public sealed record BriefingNote(Guid Id, string? Title, string Content, int? PausedForDays = null);

/// <summary>
/// One section of the briefing: the items it shows, and how many there are in all. They differ
/// when the section is capped - a briefing is a message, not a report, so a user with four hundred
/// untagged notes gets the number rather than four hundred lines (daily-briefing design.md
/// Decision 4).
/// </summary>
public sealed record BriefingSection<T>(IReadOnlyList<T> Items, int Total)
{
    public static BriefingSection<T> Empty { get; } = new([], 0);

    public bool IsEmpty => Total == 0;

    /// <summary>True when there is more behind the items shown, which the message has to say.</summary>
    public bool IsTruncated => Total > Items.Count;
}

/// <summary>
/// What one user's briefing is made of at the moment it was built. Deliberately plain data: it is
/// computed from queries, never generated, so that everything the briefing states is something the
/// database said (design.md Decision 1).
/// </summary>
public sealed record BriefingContent(
    BriefingSection<BriefingReminder> RemindersToday,
    BriefingSection<BriefingNote> InProgress,
    BriefingSection<BriefingNote> Pending,
    BriefingSection<BriefingNote> UntaggedNotes)
{
    public static BriefingContent Empty { get; } =
        new(BriefingSection<BriefingReminder>.Empty, BriefingSection<BriefingNote>.Empty,
            BriefingSection<BriefingNote>.Empty, BriefingSection<BriefingNote>.Empty);

    /// <summary>
    /// Nothing to report. The automatic briefing stays silent on such a day and resolves it
    /// anyway; one the user asked for still answers, saying so (specs/briefing).
    /// </summary>
    public bool IsEmpty => RemindersToday.IsEmpty && InProgress.IsEmpty && Pending.IsEmpty && UntaggedNotes.IsEmpty;
}

/// <summary>
/// The numbers the spec deliberately does not fix (design.md Decision 4): they decide how much a
/// briefing shows, not what it promises, so they can move without touching specs/briefing.
/// </summary>
public static class BriefingLimits
{
    /// <summary>How far back a note still counts as one the user has not got round to tagging.</summary>
    public static readonly TimeSpan UntaggedWindow = TimeSpan.FromDays(7);

    /// <summary>Items shown per section before it falls back to stating a count.</summary>
    public const int ItemsPerSection = 5;

    /// <summary>
    /// How long a note may stand paused before it rejoins the pending section, saying how long it
    /// has been paused (note-status design.md Decision 6). Pausing is meant to silence a note, but
    /// a note silenced for ever is the invisibility the briefing exists to prevent
    /// (specs/briefing - Purpose), so the silence has a bound.
    ///
    /// Here rather than in specs/briefing for the same reason <see cref="UntaggedWindow"/> is: it
    /// decides how much the briefing shows, not what it promises.
    /// </summary>
    public static readonly TimeSpan PausedResurfaceAfter = TimeSpan.FromDays(15);
}
