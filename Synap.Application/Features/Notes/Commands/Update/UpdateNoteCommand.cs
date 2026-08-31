using SergioIzq.Application.Kernel.Messaging;

namespace Synap.Application.Features.Notes.Commands.Update;

public sealed record UpdateNoteCommand(Guid NoteId, string? Title, string Content) : ICommand;
