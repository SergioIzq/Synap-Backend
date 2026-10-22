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
/// An event of the day being briefed, with the user's own notes that relate to it
/// (calendar-integration design.md Decision 5). <see cref="RelatedNotes"/> is empty when nothing
/// of theirs relates to it, and the event is still shown - an event on its own is the honest
/// answer, and a feature that invents a connection is worse than one that shows none
/// (specs/briefing "An event with no related notes").
///
/// The event itself is not stored anywhere: it lives in this record for as long as it takes to
/// render the briefing that read it (specs/calendar-integration "Calendar events are never stored").
/// </summary>
public sealed record BriefingEvent(CalendarEvent Event, IReadOnlyList<BriefingNote> RelatedNotes);

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
/// What the calendar contributed to today's briefing (calendar-integration design.md Decision 9).
/// Three cases that the message renders differently: nothing was connected, something was connected
/// but could not be read (with a flag for whether the user needs to reconnect), or the events
/// themselves. <see cref="BriefingContent.IsEmpty"/> counts events but not an unreadable notice,
/// so a day whose only entry is an unavailable calendar is still a day with nothing to report.
/// </summary>
public abstract record BriefingCalendar
{
    private BriefingCalendar() { }

    /// <summary>No calendar connected. The briefing omits the calendar section silently.</summary>
    public sealed record NotConnected : BriefingCalendar;

    /// <summary>
    /// A calendar was connected but could not be read. The message shows a one-line notice.
    /// <see cref="NeedsReconnect"/> is true when the connection itself expired and the user has to
    /// reconnect in Settings (specs/briefing "Reconnect required").
    /// </summary>
    public sealed record Unreadable(bool NeedsReconnect) : BriefingCalendar;

    /// <summary>The events for the day, possibly an empty section when the day has none.</summary>
    public sealed record Events(BriefingSection<BriefingEvent> Section) : BriefingCalendar;

    public static readonly BriefingCalendar Default = new NotConnected();
}

/// <summary>
/// What one user's briefing is made of at the moment it was built. Deliberately plain data: it is
/// computed from queries, never generated, so that everything the briefing states is something the
/// database said (design.md Decision 1).
/// </summary>
public sealed record BriefingContent(
    BriefingCalendar Calendar,
    BriefingSection<BriefingReminder> RemindersToday,
    BriefingSection<BriefingNote> InProgress,
    BriefingSection<BriefingNote> Pending,
    BriefingSection<BriefingNote> UntaggedNotes)
{
    public static BriefingContent Empty { get; } =
        new(BriefingCalendar.Default, BriefingSection<BriefingReminder>.Empty,
            BriefingSection<BriefingNote>.Empty, BriefingSection<BriefingNote>.Empty,
            BriefingSection<BriefingNote>.Empty);

    /// <summary>
    /// The events of the local day as a plain section - the same view the message's calendar
    /// section and existing tests use. Empty both when there are no events and when the calendar
    /// was not connected or could not be read; use <see cref="Calendar"/> to tell those apart.
    /// </summary>
    public BriefingSection<BriefingEvent> EventsToday =>
        Calendar is BriefingCalendar.Events e ? e.Section : BriefingSection<BriefingEvent>.Empty;

    /// <summary>
    /// Nothing to report. The automatic briefing stays silent on such a day and resolves it
    /// anyway; one the user asked for still answers, saying so (specs/briefing).
    /// An unreadable-calendar notice does not count as content
    /// (specs/briefing "A day whose only entry is an unavailable calendar is empty").
    /// </summary>
    public bool IsEmpty =>
        !HasEvents && RemindersToday.IsEmpty && InProgress.IsEmpty && Pending.IsEmpty && UntaggedNotes.IsEmpty;

    private bool HasEvents => Calendar is BriefingCalendar.Events { Section.IsEmpty: false };
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

    /// <summary>
    /// Events shown from the day's calendar before the section falls back to stating a count. The
    /// same as <see cref="ItemsPerSection"/> - a day with more than five events is a day whose
    /// briefing is better off saying so than listing it (calendar-integration design.md Decision 6).
    /// </summary>
    public const int EventsPerBriefing = ItemsPerSection;

    /// <summary>
    /// Related notes hung off one event. Smaller than <see cref="ItemsPerSection"/> on purpose: the
    /// notes of an event are an aside within a line, not a section, and a day with five events each
    /// carrying five notes is a report rather than a message (calendar-integration design.md
    /// Decision 6).
    ///
    /// Here rather than in specs/briefing for the same reason <see cref="PausedResurfaceAfter"/> is:
    /// it decides how much the briefing shows, not what it promises.
    /// </summary>
    public const int RelatedNotesPerEvent = 2;

    /// <summary>
    /// How close in meaning a note has to be to an event's title before it is shown under that
    /// event (calendar-integration design.md Decision 5). Above the AI service's own keep rule on
    /// purpose: that rule decides what is worth showing for a question the user asked, and an
    /// event is not a question - nobody asked for these notes, so a weak match here is noise the
    /// user has to read past rather than an answer they can judge. Without a floor above it, every
    /// event drags in the least-unrelated notes in the vault and the briefing starts making
    /// connections that are not there.
    ///
    /// A note found by its exact words carries no similarity at all and is kept regardless: a note
    /// titled "Migrar auth a OAuth" under a meeting called "Revisión OAuth" is related on the word,
    /// which is one of the reasons the search is the one the assistant uses.
    ///
    /// Here rather than in specs/briefing for the same reason <see cref="PausedResurfaceAfter"/> is:
    /// it decides how much the briefing shows, not what it promises. Settled against a real vault
    /// (task 9.6).
    /// </summary>
    public const double RelatedNoteSimilarityFloor = 0.55;
}
