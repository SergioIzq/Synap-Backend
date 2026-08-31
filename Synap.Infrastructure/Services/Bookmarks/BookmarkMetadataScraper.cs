using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Services.Bookmarks;

/// <summary>
/// Fetches a bookmark's linked page and extracts title/description/preview image
/// (specs/knowledge-vault "Bookmark metadata enrichment"). Prefers Open Graph tags, falling
/// back to the plain &lt;title&gt;/meta description - never throws for a normal fetch/parse
/// failure, so an unreachable link just means no metadata (per spec), not a crashed job.
/// </summary>
public sealed class BookmarkMetadataScraper : IBookmarkMetadataScraper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BookmarkMetadataScraper> _logger;

    public BookmarkMetadataScraper(HttpClient httpClient, ILogger<BookmarkMetadataScraper> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<NoteMetadata?> ScrapeAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var document = new HtmlDocument();
            document.LoadHtml(html);

            var title = MetaContent(document, "og:title") ?? InnerText(document, "//title");
            if (string.IsNullOrWhiteSpace(title))
            {
                // No usable title at all - treat as "could not extract metadata" per spec,
                // rather than attaching an empty/meaningless metadata value.
                return null;
            }

            var description = MetaContent(document, "og:description") ?? MetaName(document, "description");
            var imageUrl = MetaContent(document, "og:image");

            return NoteMetadata.Create(title.Trim(), description?.Trim(), imageUrl?.Trim());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not scrape bookmark metadata for {Url}", url);
            return null;
        }
    }

    private static string? MetaContent(HtmlDocument document, string property)
    {
        var value = document.DocumentNode.SelectSingleNode($"//meta[@property='{property}']")?.GetAttributeValue("content", string.Empty);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? MetaName(HtmlDocument document, string name)
    {
        var value = document.DocumentNode.SelectSingleNode($"//meta[@name='{name}']")?.GetAttributeValue("content", string.Empty);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? InnerText(HtmlDocument document, string xpath)
        => document.DocumentNode.SelectSingleNode(xpath)?.InnerText;
}
