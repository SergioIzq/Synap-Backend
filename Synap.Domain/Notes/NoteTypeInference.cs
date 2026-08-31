namespace Synap.Domain;

/// <summary>Used by quick-capture (specs/knowledge-vault) when the caller doesn't specify a type explicitly.</summary>
public static class NoteTypeInference
{
    public static NoteType Infer(string content)
    {
        var trimmed = content.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            !trimmed.Contains(' ') && !trimmed.Contains('\n'))
        {
            return NoteType.Bookmark;
        }

        return NoteType.Text;
    }
}
