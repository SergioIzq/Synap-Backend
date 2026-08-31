using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Notes.Commands.Create;

public sealed record CreateNoteCommand(NoteType Type, string? Title, string Content) : ICommand<Guid>;
