using Dapper;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Persistence.Data.Briefing;

/// <summary>
/// The briefing's three queries (daily-briefing design.md Decision 4), Dapper like the other
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
            SELECT n.id AS Id, n.title AS Title, n.content AS Content,
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

    public async Task<BriefingSection<BriefingNote>> ListOpenThreadNotesAsync(
        Guid userId,
        IReadOnlyList<string> stemmedMarkers,
        IReadOnlyList<string> literalMarkers,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (stemmedMarkers.Count == 0 && literalMarkers.Count == 0)
        {
            return BriefingSection<BriefingNote>.Empty;
        }

        using var connection = _dbConnectionFactory.CreateConnection();

        // Two ways of matching, for one reason: the search vector is built with the Spanish
        // configuration, which drops "todo" as a stopword - the marker a technical note is most
        // likely to carry. websearch_to_tsquery takes the stemmable ones as free text ("a OR b"),
        // safely; the rest are matched as substrings over the rows the user filter already cut
        // down (design.md Decision 4).
        //
        // LIKE, not ILIKE, and that is the whole point: "TODO" means something as a convention
        // only in capitals. Case-insensitively it is the Spanish word "todo", which turns up in
        // "todo salió bien" and "sobre todo" - matching that would put half the vault in the
        // section and make it worthless.
        const string sql = """
            WITH q AS (
                SELECT CASE WHEN @Stemmed IS NULL THEN NULL
                            ELSE websearch_to_tsquery('public.spanish_unaccent', @Stemmed) END AS query
            )
            SELECT n.id AS Id, n.title AS Title, n.content AS Content,
                   COUNT(*) OVER() AS TotalCount
            FROM notes n, q
            WHERE n.user_id = @UserId
              AND (
                    (q.query IS NOT NULL AND n.search_vector @@ q.query)
                 OR (@Literal IS NOT NULL AND (
                        n.content LIKE ANY (@Literal) OR n.title LIKE ANY (@Literal)))
              )
            ORDER BY n.created_at DESC, n.id
            LIMIT @Limit
            """;

        var parameters = new
        {
            UserId = userId,
            Stemmed = stemmedMarkers.Count == 0 ? null : string.Join(" OR ", stemmedMarkers),
            Literal = literalMarkers.Count == 0 ? null : literalMarkers.Select(m => $"%{m}%").ToArray(),
            Limit = limit,
        };

        return await NotesAsync(connection, sql, parameters, cancellationToken);
    }

    private static async Task<BriefingSection<BriefingNote>> NotesAsync(
        System.Data.IDbConnection connection, string sql, object parameters, CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<NoteRow>(new CommandDefinition(
            sql, parameters, cancellationToken: cancellationToken))).ToList();

        return new BriefingSection<BriefingNote>(
            rows.Select(r => new BriefingNote(r.Id, r.Title, r.Content)).ToList(),
            rows.Count == 0 ? 0 : (int)rows[0].TotalCount);
    }

    // COUNT(*) OVER() is a bigint, so the row takes a long and the section narrows it: a section
    // holds at most a few items and a total nobody will ever push past int.
    private sealed record ReminderRow(Guid Id, string Text, DateTime DueAt, string? NoteTitle, long TotalCount);

    private sealed record NoteRow(Guid Id, string? Title, string Content, long TotalCount);
}
