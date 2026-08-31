namespace Synap.Domain;

public sealed record RelatedNote(Guid Id, string? Title, string Content, NoteType Type, double Similarity);
