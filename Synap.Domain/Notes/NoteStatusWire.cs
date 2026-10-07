namespace Synap.Domain;

/// <summary>
/// Parsing for a single status arriving as a wire name, where the caller needs to answer with its
/// own error rather than let a deserialization failure surface as a generic 400 - creating a note
/// with an invalid status has to be rejected with a Spanish message, like a blank tag is
/// (specs/knowledge-vault "Create a note with an invalid status").
/// </summary>
public static class NoteStatusWire
{
    /// <summary>
    /// Null or blank yields a null status, which is the specified default - a note with no status.
    /// Matching is case-insensitive and by name only, so "7" is rejected.
    /// </summary>
    public static bool TryParse(string? value, out NoteStatus? status)
    {
        status = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.GetNames<NoteStatus>().FirstOrDefault(n => string.Equals(n, value.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } name)
        {
            return false;
        }

        status = Enum.Parse<NoteStatus>(name);
        return true;
    }

    /// <summary>The four accepted wire names, for a validation message.</summary>
    public static IReadOnlyList<string> AcceptedValues { get; } =
        [.. Enum.GetNames<NoteStatus>().Select(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName)];
}
