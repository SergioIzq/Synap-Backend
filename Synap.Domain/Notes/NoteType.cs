namespace Synap.Domain;

/// <summary>
/// Serialized as "text" / "codeSnippet" / "bookmark" through <see cref="NoteTypeJsonConverter"/>
/// on the response properties - not with [JsonStringEnumMemberName] here, which would make
/// input exact-match only and break clients sending "CodeSnippet" (e.g. the iOS Shortcut).
/// </summary>
public enum NoteType
{
    Text,
    CodeSnippet,
    Bookmark,
}
