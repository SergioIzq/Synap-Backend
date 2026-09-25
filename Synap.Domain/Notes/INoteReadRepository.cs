namespace Synap.Domain;

public interface INoteReadRepository
{
    Task<PagedResult<NoteSearchResult>> SearchAsync(
        Guid userId, NoteSearchCriteria criteria, CancellationToken cancellationToken = default);

    /// <summary>Null when the note doesn't exist or belongs to someone else - callers can't tell which.</summary>
    Task<NoteSearchResult?> GetByIdAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>The user's tag names that are on at least one note, alphabetically.</summary>
    Task<IReadOnlyList<string>> ListTagsAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Already-validated search input: Page >= 1, PageSize within NoteSearchCriteria's bounds.</summary>
public sealed record NoteSearchCriteria(string? SearchTerm, string? Tag, NoteType? Type, int Page, int PageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
}
