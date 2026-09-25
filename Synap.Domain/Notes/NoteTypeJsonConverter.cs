using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synap.Domain;

/// <summary>
/// Writes camelCase ("codeSnippet") and reads any casing. Applied per property on response
/// shapes because the kernel's result handler serializes enums with its own PascalCase options,
/// ignoring Program.cs's camelCase converter; a property-level converter wins over both.
/// </summary>
public sealed class NoteTypeJsonConverter : JsonConverter<NoteType>
{
    public override NoteType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return Enum.GetNames<NoteType>().FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<NoteType>(name)
            : throw new JsonException($"Unknown note type '{value}'.");
    }

    public override void Write(Utf8JsonWriter writer, NoteType value, JsonSerializerOptions options)
        => writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
