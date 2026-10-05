using Dapper;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Persistence.Data.Briefing;

/// <summary>
/// The briefing's four queries (daily-briefing design.md Decision 4, extended by note-status
/// Decision 7), Dapper like the other
/// reporting-style reads. Each one filters by user first and takes its total in the same round
/// trip with COUNT(*) OVER(), so a capped section still knows how many it stands for.
/// </summary>
public sealed class BriefingReadRepository : IBriefingReadRepository
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public BriefingReadRepository(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    public async Task<BriefingSection<BriefingReminder>> ListRemindersDueAsync(
        Guid userId, DateTime fromUtc, DateTime toUtcExclusive, int limit, CancellationToken cancellationToken = default)
    {
        using var connection = _dbConnectionFactory.CreateConnection();

        // Pending only: one already sent is not something the user still has ahead of them. The
        // bounds are half-open so a reminder at the stroke of midnight belongs to one day only.
        const string sql = """
            SELECT r.id AS Id, r.text AS Text, r.due_at AS DueAt, n.title AS NoteTitle,
                   COUNT(*) OVER() AS TotalCount
            FROM reminders r
            LEFT JOIN notes n ON n.id = r.note_id
            WHERE r.user_id = @UserId
              AND r.sent_at IS NULL
              AND r.due_at >= @FromUtc
              AND r.due_at < @ToUtc
            ORDER BY r.due_at, r.id
            LIMIT @Limit
            """;

        var rows = (await connection.QueryAsync<ReminderRow>(new CommandDefinition(
            sql,
            new { UserId = userId, FromUtc = fromUtc, ToUtc = toUtcExclusive, Limit = limit },
            cancellationToken: cancellationToken))).ToList();

        return new BriefingSection<BriefingReminder>(
            rows.Select(r => new BriefingReminder(r.Id, r.Text, r.DueAt, r.NoteTitle)).ToList(),
            rows.Count == 0 ? 0 : (int)rows[0].TotalCount);
    }

    public async Task<BriefingSection<BriefingNote>> ListUntaggedNotesAsync(
        Guid userId, DateTime sinceUtc, int limit, CancellationToken cancellationToken = default)
    {
        using var connection = _dbConnectionFactory.CreateConnection();

        const string sql = """
            SELECT n.id AS Id, n.title AS Title, n.content AS Content, NULL::int AS PausedForDays,
                   COUNT(*) OVER() AS TotalCount
            FROM notes n
            WHERE n.user_id = @UserId
              AND n.created_at >= @SinceUtc
              AND NOT EXISTS (SELECT 1 FROM note_tags nt WHERE nt.note_id = n.id)
            ORDER BY n.created_at DESC, n.id
            LIMIT @Limit
            """;

        return await NotesAsync(connection, sql, new { UserId = userId, SinceUtc = sinceUtc, Limit = limit }, cancellationToken);
    }

    /// <summary>
    /// What the user has underway. No text is inspected: the status is a fact the user recorded,
    /// which is the whole point of replacing the old marker heuristic (note-status design.md
    /// Decision 7).
    /// </summary>
    public async Task<BriefingSection<BriefingNote>> ListInProgressNotesAsync(
        Guid userId, int limit, CancellationToken cancellationToken = default)
    {
        using var connection = _dbConnectionFactory.CreateConnection();

        const string sql = """
            SELECT n.id AS Id, n.title AS Title, n.content AS Content, NULL::int AS PausedForDays,
                   COUNT(*) OVER() AS TotalCount
            FROM notes n
            WHERE n.user_id = @UserId
              AND n.status = 'InProgress'
            ORDER BY n.created_at DESC, n.id
            LIMIT @Limit
            """;

        return await NotesAsync(connection, sql, new { UserId = userId, Limit = limit }, cancellationToken);
    }

    /// <summary>
    /// The queue: notes marked pending, plus the paused ones that have stood paused since before
    /// <paramref name="pausedBeforeUtc"/>, each carrying how many days that has been. A note paused
    /// more recently is absent - pausing is what silences it (design.md Decision 6).
    ///
    /// A resurfaced note joins pending rather than in-progress on purpose: nothing has touched it
    /// in over two weeks, so it is queued, not underway.
    /// </summary>
    public async Task<BriefingSection<BriefingNote>> ListPendingNotesAsync(
        Guid userId, DateTime pausedBeforeUtc, DateTime nowUtc, int limit, CancellationToken cancellationToken = default)
    {
        using var connection = _dbConnectionFactory.CreateConnection();

        // status_changed_at is never null for a note that has a status (Note.SetStatus sets both),
        // but the comparison is written so that a null would exclude the note rather than resurface
        // it with a nonsensical age.
        const string sql = """
            SELECT n.id AS Id, n.title AS Title, n.content AS Content,
                   CASE WHEN n.status = 'Paused'
                        THEN EXTRACT(DAY FROM @NowUtc - n.status_changed_at)::int
                        END AS PausedForDays,
                   COUNT(*) OVER() AS TotalCount
            FROM notes n
            WHERE n.user_id = @UserId
              AND (
                    n.status = 'Pending'
                 OR (n.status = 'Paused'
                     AND n.status_changed_at IS NOT NULL
                     AND n.status_changed_at < @PausedBeforeUtc)
              )
            ORDER BY n.created_at DESC, n.id
            LIMIT @Limit
            """;

        return await NotesAsync(
            connection,
            sql,
            new { UserId = userId, PausedBeforeUtc = pausedBeforeUtc, NowUtc = nowUtc, Limit = limit },
            cancellationToken);
    }

    private static async Task<BriefingSection<BriefingNote>> NotesAsync(
        System.Data.IDbConnection connection, string sql, object parameters, CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<NoteRow>(new CommandDefinition(
            sql, parameters, cancellationToken: cancellationToken))).ToList();

        return new BriefingSection<BriefingNote>(
            rows.Select(r => new BriefingNote(r.Id, r.Title, r.Content, r.PausedForDays)).ToList(),
            rows.Count == 0 ? 0 : (int)rows[0].TotalCount);
    }

    // COUNT(*) OVER() is a bigint, so the row takes a long and the section narrows it: a section
    // holds at most a few items and a total nobody will ever push past int.
    private sealed record ReminderRow(Guid Id, string Text, DateTime DueAt, string? NoteTitle, long TotalCount);

    private sealed record NoteRow(Guid Id, string? Title, string Content, int? PausedForDays, long TotalCount);
}
