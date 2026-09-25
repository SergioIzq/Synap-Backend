namespace Synap.Domain;

public sealed record RelatedNote(
    Guid Id,
    string? Title,
    string Content,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(NoteTypeJsonConverter))] NoteType Type,
    double Similarity);
