using SergioIzq.Application.Kernel.Messaging;

namespace Synap.Application.Features.Notes.Commands.Delete;

public sealed record DeleteNoteCommand(Guid NoteId) : ICommand;
