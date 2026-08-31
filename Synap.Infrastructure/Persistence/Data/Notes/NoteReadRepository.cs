using Dapper;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Persistence.Data.Notes;

/// <summary>
/// Dapper + Postgres full-text search (specs/knowledge-vault "Full-text search") - the one
/// genuine reporting-style read in the MVP, unlike Identity's EF-based reads (see
/// design.md Decision 7 amendment and UserReadRepository's own comment on the distinction).
/// `to_tsvector`/`plainto_tsquery` run on the fly rather than against a stored/indexed tsvector
/// column - fine at this data volume; revisit with a generated column + GIN index if search
/// ever gets slow.
/// </summary>
public sealed class NoteReadRepository : INoteReadRepository
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public NoteReadRepository(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    public async Task<IReadOnlyList<NoteSearchResult>> SearchAsync(
        Guid userId, string? searchTerm, string? tag, CancellationToken cancellationToken = default)
    {
        using var connection = _dbConnectionFactory.CreateConnection();

        const string notesSql = """
            SELECT n.id AS Id, n.title AS Title, n.content AS Content, n.note_type AS Type,
                   n.created_at AS CreatedAt, n.updated_at AS UpdatedAt,
                   n.metadata_title AS MetadataTitle, n.metadata_description AS MetadataDescription,
                   n.metadata_image_url AS MetadataImageUrl
            FROM notes n
            WHERE n.user_id = @UserId
              AND (@SearchTerm IS NULL OR to_tsvector('english', coalesce(n.title, '') || ' ' || n.content)
                     @@ plainto_tsquery('english', @SearchTerm))
              AND (@Tag IS NULL OR EXISTS (
                    SELECT 1 FROM note_tags nt
                    JOIN tags t ON t.id = nt.tag_id
                    WHERE nt.note_id = n.id AND t.name = @Tag))
            ORDER BY
              CASE WHEN @SearchTerm IS NOT NULL
                   THEN ts_rank(to_tsvector('english', coalesce(n.title, '') || ' ' || n.content),
                                plainto_tsquery('english', @SearchTerm))
              END DESC NULLS LAST,
              n.created_at DESC
            """;

        var rows = (await connection.QueryAsync<NoteRow>(new CommandDefinition(
            notesSql,
            new { UserId = userId, SearchTerm = searchTerm, Tag = tag },
            cancellationToken: cancellationToken))).ToList();

        if (rows.Count == 0)
        {
            return [];
        }

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

    private sealed record NoteRow(
        Guid Id, string? Title, string Content, string Type, DateTime CreatedAt, DateTime UpdatedAt,
        string? MetadataTitle, string? MetadataDescription, string? MetadataImageUrl);

    private sealed record NoteTagRow(Guid NoteId, string Name);
}
