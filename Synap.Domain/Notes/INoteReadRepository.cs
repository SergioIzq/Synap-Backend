namespace Synap.Domain;

public interface INoteReadRepository
{
    Task<IReadOnlyList<NoteSearchResult>> SearchAsync(
        Guid userId, string? searchTerm, string? tag, CancellationToken cancellationToken = default);
}
