namespace Synap.Domain;

/// <summary>Plain read-model shape for search results - not the Note aggregate itself, so the
/// read side (Dapper, full-text search) never needs to reconstruct a private-constructor entity.</summary>
public sealed record NoteSearchResult(
    Guid Id,
    string? Title,
    string Content,
    NoteType Type,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<string> Tags,
    string? MetadataTitle,
    string? MetadataDescription,
    string? MetadataImageUrl);
