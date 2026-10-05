using SergioIzq.Application.Kernel.Messaging;

namespace Synap.Application.Features.Notes.Commands.SetStatus;

/// <summary>
/// specs/knowledge-vault "Note status" - a command of its own rather than part of updating the
/// note, so that marking a note does not look like editing it (note-status design.md Decision 5).
///
/// <paramref name="Status"/> is a wire name ("pending", "inProgress", "paused", "completed"), or
/// null to clear the status and leave the note as material rather than work.
/// </summary>
public sealed record SetNoteStatusCommand(Guid NoteId, string? Status) : ICommand;
