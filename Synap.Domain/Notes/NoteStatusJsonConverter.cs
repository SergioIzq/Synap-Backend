using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synap.Domain;

/// <summary>
/// Writes camelCase ("inProgress") and reads any casing, exactly as
/// <see cref="NoteTypeJsonConverter"/> does - an exact-match enum breaks clients that send
/// "Pending", which is the mistake the iOS Shortcut already made once with note types.
/// </summary>
public sealed class NoteStatusJsonConverter : JsonConverter<NoteStatus>
{
    public override NoteStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return Enum.GetNames<NoteStatus>().FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<NoteStatus>(name)
            : throw new JsonException($"Unknown note status '{value}'.");
    }

    public override void Write(Utf8JsonWriter writer, NoteStatus value, JsonSerializerOptions options)
        => writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
