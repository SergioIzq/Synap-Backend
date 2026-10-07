using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Notes.Commands.Create;

/// <summary>
/// specs/knowledge-vault "Capture a note": title, tags and a status in the same operation; a
/// missing Type is inferred from the content (a lone URL becomes a bookmark).
///
/// Status is a wire name ("pending", "inProgress", "paused", "completed") rather than the enum, so
/// an unknown value is answered with a Spanish validation message by the handler instead of a
/// generic deserialization 400 - the same treatment a blank tag gets.
/// </summary>
public sealed record CreateNoteCommand(
    NoteType? Type,
    string? Title,
    string Content,
    IReadOnlyList<string>? Tags = null,
    string? Status = null) : ICommand<Guid>;
