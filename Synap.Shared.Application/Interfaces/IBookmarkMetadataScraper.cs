using Synap.Domain;

namespace Synap.Shared.Application.Interfaces;

/// <summary>
/// Extracts title/description/preview image from a bookmark's linked page (specs/knowledge-vault
/// "Bookmark metadata enrichment"). Returns null - never throws for a normal fetch/parse failure -
/// so the caller can leave the note without metadata rather than treating an unreachable link as
/// an error.
/// </summary>
public interface IBookmarkMetadataScraper
{
    Task<NoteMetadata?> ScrapeAsync(string url, CancellationToken cancellationToken = default);
}
