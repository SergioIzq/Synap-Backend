namespace Synap.Domain;

/// <summary>
/// Value object: metadata scraped from a bookmark's linked page (see specs/knowledge-vault
/// "Bookmark metadata enrichment"). Immutable - re-scraping replaces the whole value rather than
/// mutating individual fields. Mapped as an EF Core owned type on <see cref="Note"/>.
/// </summary>
public sealed record NoteMetadata
{
    public string ExtractedTitle { get; }
    public string? ExtractedDescription { get; }
    public string? ImageUrl { get; }

    private NoteMetadata(string extractedTitle, string? extractedDescription, string? imageUrl)
    {
        ExtractedTitle = extractedTitle;
        ExtractedDescription = extractedDescription;
        ImageUrl = imageUrl;
    }

    public static NoteMetadata Create(string extractedTitle, string? extractedDescription, string? imageUrl)
        => new(extractedTitle, extractedDescription, imageUrl);
}
