namespace Synap.Domain;

/// <summary>
/// How a note is named when something has to point at it without showing it: an answer's sources,
/// and the briefing's lists. One rule in one place, so the same note reads the same wherever it is
/// mentioned.
/// </summary>
public static class NoteDisplay
{
    public const int PreviewChars = 60;

    /// <summary>A note's title, or a short preview of its text when it has none.</summary>
    public static string Label(string? title, string text)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= PreviewChars ? flat : flat[..PreviewChars].TrimEnd() + "…";
    }
}
