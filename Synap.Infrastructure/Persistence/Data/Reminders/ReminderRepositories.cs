using Microsoft.EntityFrameworkCore;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Data.Reminders;

public sealed class ReminderWriteRepository : AbsWriteRepository<Reminder, ReminderId>, IReminderWriteRepository
{
    private readonly SynapDbContext _context;

    public ReminderWriteRepository(SynapDbContext context) : base(context)
    {
        _context = context;
    }

    public Task<Reminder?> GetOwnedByUserAsync(Guid reminderId, Guid userId, CancellationToken cancellationToken = default)
    {
        var id = ReminderId.CreateFromDatabase(reminderId);
        var owner = UserId.CreateFromDatabase(userId);
        return _context.Set<Reminder>().AsTracking().FirstOrDefaultAsync(r => r.Id == id && r.UserId == owner, cancellationToken);
    }

    public async Task<IReadOnlyList<DueReminder>> ListDueAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default)
    {
        // The filtered index idx_reminders_due_pending covers this. DismissedAt is checked too so a
        // series cancelled before its first delivery can never be picked up.
        var due = await _context.Set<Reminder>()
            .AsTracking()
            .Where(r => r.DueAt <= nowUtc && r.SentAt == null && r.DismissedAt == null)
            .OrderBy(r => r.DueAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return [];
        }

        // Owners and note titles in one round trip each instead of one per reminder: a tick
        // delivers a handful of rows, usually all for the same user.
        var ownerIds = due.Select(r => r.UserId).Distinct().ToList();
        var owners = await _context.Set<User>()
            .AsNoTracking()
            .Where(u => ownerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.TelegramChatId, u.Timezone })
            .ToDictionaryAsync(u => u.Id, cancellationToken);

        var titles = await NoteTitlesAsync(due, cancellationToken);

        return due.Select(reminder =>
        {
            owners.TryGetValue(reminder.UserId, out var owner);
            return new DueReminder(reminder, owner?.TelegramChatId, owner?.Timezone, TitleOf(reminder, titles));
        }).ToList();
    }

    private async Task<Dictionary<NoteId, string?>> NoteTitlesAsync(List<Reminder> reminders, CancellationToken cancellationToken)
    {
        var noteIds = reminders.Where(r => r.NoteId is not null).Select(r => r.NoteId!.Value).Distinct().ToList();
        if (noteIds.Count == 0)
        {
            return [];
        }

        return await _context.Set<Note>()
            .AsNoTracking()
            .Where(n => noteIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.Title, cancellationToken);
    }

    internal static string? TitleOf(Reminder reminder, Dictionary<NoteId, string?> titles)
        => reminder.NoteId is { } noteId && titles.TryGetValue(noteId, out var title) ? title : null;
}

public sealed class ReminderReadRepository : IReminderReadRepository
{
    private readonly SynapDbContext _context;

    public ReminderReadRepository(SynapDbContext context)
    {
        _context = context;
    }

    public Task<IReadOnlyList<ReminderSummary>> ListPendingByUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => ListAsync(userId, noteId: null, cancellationToken);

    public Task<IReadOnlyList<ReminderSummary>> ListByNoteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
        => ListAsync(userId, NoteId.CreateFromDatabase(noteId), cancellationToken);

    /// <summary>
    /// Pending reminders, soonest first. Scoping by user id means a note that isn't theirs simply
    /// has none (specs/reminders "Reminders never cross users").
    /// </summary>
    private async Task<IReadOnlyList<ReminderSummary>> ListAsync(Guid userId, NoteId? noteId, CancellationToken cancellationToken)
    {
        var owner = UserId.CreateFromDatabase(userId);
        var query = _context.Set<Reminder>()
            .AsNoTracking()
            .Where(r => r.UserId == owner && r.DismissedAt == null);

        if (noteId is { } note)
        {
            query = query.Where(r => r.NoteId == note);
        }

        // Mapped after loading, like memory entries: EF can't translate `.Value` on a
        // value-converted key (see AbsWriteRepository), and the list is small.
        var reminders = await query
            .OrderBy(r => r.DueAt)
            .ThenBy(r => r.FechaCreacion)
            .ToListAsync(cancellationToken);

        var titles = await TitlesAsync(reminders, cancellationToken);
        return reminders.Select(r => ReminderSummary.From(r, ReminderWriteRepository.TitleOf(r, titles))).ToList();
    }

    private async Task<Dictionary<NoteId, string?>> TitlesAsync(List<Reminder> reminders, CancellationToken cancellationToken)
    {
        var noteIds = reminders.Where(r => r.NoteId is not null).Select(r => r.NoteId!.Value).Distinct().ToList();
        if (noteIds.Count == 0)
        {
            return [];
        }

        return await _context.Set<Note>()
            .AsNoTracking()
            .Where(n => noteIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.Title, cancellationToken);
    }
}
