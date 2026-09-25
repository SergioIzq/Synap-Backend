using Dapper;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Persistence.Data.Notes;

/// <summary>
/// Dapper + Postgres full-text search (specs/knowledge-vault "Full-text search") - the one
/// genuine reporting-style read, unlike Identity's EF-based reads (see design.md Decision 7
/// amendment and UserReadRepository's own comment on the distinction). Matches against the
/// indexed notes.search_vector column (Spanish, accent-insensitive - migration
/// AddNoteSearchVector) and pages with LIMIT/OFFSET; COUNT(*) OVER() returns the total in the
/// same round trip.
/// </summary>
public sealed class NoteReadRepository : INoteReadRepository
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public NoteReadRepository(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    public async Task<PagedResult<NoteSearchResult>> SearchAsync(
        Guid userId, NoteSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        using var connection = _dbConnectionFactory.CreateConnection();

        // websearch_to_tsquery accepts free text safely (quotes, -exclusion, OR) and never
        // throws on user input, unlike to_tsquery.
        const string notesSql = """
            WITH q AS (
                SELECT CASE WHEN @SearchTerm IS NULL THEN NULL
                            ELSE websearch_to_tsquery('public.spanish_unaccent', @SearchTerm) END AS query
            )
            SELECT n.id AS Id, n.title AS Title, n.content AS Content, n.note_type AS Type,
                   n.created_at AS CreatedAt, n.updated_at AS UpdatedAt,
                   n.metadata_title AS MetadataTitle, n.metadata_description AS MetadataDescription,
                   n.metadata_image_url AS MetadataImageUrl,
                   COUNT(*) OVER() AS TotalCount
            FROM notes n, q
            WHERE n.user_id = @UserId
              AND (q.query IS NULL OR n.search_vector @@ q.query)
              AND (@Type IS NULL OR n.note_type = @Type)
              AND (@Tag IS NULL OR EXISTS (
                    SELECT 1 FROM note_tags nt
                    JOIN tags t ON t.id = nt.tag_id
                    WHERE nt.note_id = n.id AND t.name = @Tag))
            ORDER BY
              CASE WHEN q.query IS NOT NULL THEN ts_rank(n.search_vector, q.query) END DESC NULLS LAST,
              n.created_at DESC,
              n.id
            LIMIT @PageSize OFFSET @Offset
            """;

        var rows = (await connection.QueryAsync<NoteRow>(new CommandDefinition(
            notesSql,
            new
            {
                UserId = userId,
                criteria.SearchTerm,
                criteria.Tag,
                Type = criteria.Type?.ToString(),
                criteria.PageSize,
                Offset = (criteria.Page - 1) * criteria.PageSize,
            },
            cancellationToken: cancellationToken))).ToList();

        if (rows.Count == 0)
        {
            // Past the last page COUNT(*) OVER() has no row to ride on - fetch the total apart so
            // the client still learns how many matches exist.
            var total = criteria.Page == 1 ? 0 : await CountAsync(connection, userId, criteria, cancellationToken);
            return new PagedResult<NoteSearchResult>([], criteria.Page, criteria.PageSize, total);
        }

        var items = await WithTagsAsync(connection, rows, cancellationToken);

        return new PagedResult<NoteSearchResult>(items, criteria.Page, criteria.PageSize, (int)rows[0].TotalCount);
    }

    public async Task<NoteSearchResult?> GetByIdAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        using var connection = _dbConnectionFactory.CreateConnection();

        const string noteSql = """
            SELECT n.id AS Id, n.title AS Title, n.content AS Content, n.note_type AS Type,
                   n.created_at AS CreatedAt, n.updated_at AS UpdatedAt,
                   n.metadata_title AS MetadataTitle, n.metadata_description AS MetadataDescription,
                   n.metadata_image_url AS MetadataImageUrl,
                   1::bigint AS TotalCount
            FROM notes n
            WHERE n.id = @NoteId AND n.user_id = @UserId
            """;

        var row = await connection.QuerySingleOrDefaultAsync<NoteRow>(new CommandDefinition(
            noteSql, new { NoteId = noteId, UserId = userId }, cancellationToken: cancellationToken));

        return row is null ? null : (await WithTagsAsync(connection, [row], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        using var connection = _dbConnectionFactory.CreateConnection();

        // Only tags still on a note: deleting a note leaves its tag rows behind, and offering
        // those in the filter would always lead to an empty result.
        const string tagsSql = """
            SELECT t.name
            FROM tags t
            WHERE t.user_id = @UserId
              AND EXISTS (SELECT 1 FROM note_tags nt WHERE nt.tag_id = t.id)
            ORDER BY t.name
            """;

        return (await connection.QueryAsync<string>(new CommandDefinition(
            tagsSql, new { UserId = userId }, cancellationToken: cancellationToken))).ToList();
    }

    private static async Task<List<NoteSearchResult>> WithTagsAsync(
        System.Data.IDbConnection connection, IReadOnlyList<NoteRow> rows, CancellationToken cancellationToken)
    {
        const string tagsSql = """
            SELECT nt.note_id AS NoteId, t.name AS Name
            FROM note_tags nt
            JOIN tags t ON t.id = nt.tag_id
            WHERE nt.note_id = ANY(@NoteIds)
            """;

        var tagRows = await connection.QueryAsync<NoteTagRow>(new CommandDefinition(
            tagsSql,
            new { NoteIds = rows.Select(r => r.Id).ToArray() },
            cancellationToken: cancellationToken));

        var tagsByNoteId = tagRows
            .GroupBy(t => t.NoteId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(t => t.Name).ToList());

        return rows
            .Select(r => new NoteSearchResult(
                r.Id,
                r.Title,
                r.Content,
                Enum.Parse<NoteType>(r.Type),
                r.CreatedAt,
                r.UpdatedAt,
                tagsByNoteId.GetValueOrDefault(r.Id, []),
                r.MetadataTitle,
                r.MetadataDescription,
                r.MetadataImageUrl))
            .ToList();
    }

    private static Task<int> CountAsync(
        System.Data.IDbConnection connection, Guid userId, NoteSearchCriteria criteria, CancellationToken cancellationToken)
    {
        const string countSql = """
            SELECT COUNT(*)::int
            FROM notes n
            WHERE n.user_id = @UserId
              AND (@SearchTerm IS NULL OR n.search_vector @@ websearch_to_tsquery('public.spanish_unaccent', @SearchTerm))
              AND (@Type IS NULL OR n.note_type = @Type)
              AND (@Tag IS NULL OR EXISTS (
                    SELECT 1 FROM note_tags nt
                    JOIN tags t ON t.id = nt.tag_id
                    WHERE nt.note_id = n.id AND t.name = @Tag))
            """;

        return connection.ExecuteScalarAsync<int>(new CommandDefinition(
            countSql,
            new { UserId = userId, criteria.SearchTerm, criteria.Tag, Type = criteria.Type?.ToString() },
            cancellationToken: cancellationToken));
    }

    private sealed record NoteRow(
        Guid Id, string? Title, string Content, string Type, DateTime CreatedAt, DateTime UpdatedAt,
        string? MetadataTitle, string? MetadataDescription, string? MetadataImageUrl, long TotalCount);

    private sealed record NoteTagRow(Guid NoteId, string Name);
}
