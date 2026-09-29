using SergioIzq.Domain.Kernel.Interfaces.Repositories;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Domain;

public interface IReminderWriteRepository : IWriteRepository<Reminder, ReminderId>
{
    /// <summary>Tracked; null when the reminder doesn't exist or belongs to someone else - callers can't tell which.</summary>
    Task<Reminder?> GetOwnedByUserAsync(Guid reminderId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tracked, for the delivery poller: reminders due at or before <paramref name="nowUtc"/> that
    /// have not been delivered yet, soonest first, with the owner's Telegram chat and timezone -
    /// one query instead of a user lookup per reminder (design.md Decision 1).
    /// </summary>
    Task<IReadOnlyList<DueReminder>> ListDueAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default);
}

public interface IReminderReadRepository
{
    /// <summary>The user's pending reminders, soonest first.</summary>
    Task<IReadOnlyList<ReminderSummary>> ListPendingByUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The pending reminders on one of the user's notes, soonest first; empty for a note that isn't theirs.</summary>
    Task<IReadOnlyList<ReminderSummary>> ListByNoteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <paramref name="Recurrence"/> is the stored string (null for a one-off) and
/// <paramref name="NoteTitle"/> the linked note's displayable title, null when there is no link.
/// </summary>
public sealed record ReminderSummary(
    Guid Id,
    string Text,
    DateTime DueAt,
    string? Recurrence,
    Guid? NoteId,
    string? NoteTitle)
{
    public static ReminderSummary From(Reminder reminder, string? noteTitle = null)
        => new(reminder.Id.Value, reminder.Text, reminder.DueAt, reminder.Recurrence?.ToString(), reminder.NoteId?.Value, noteTitle);
}

/// <summary>A reminder the poller has to deliver, with what it needs about its owner.</summary>
public sealed record DueReminder(Reminder Reminder, string? TelegramChatId, string? Timezone, string? NoteTitle);
