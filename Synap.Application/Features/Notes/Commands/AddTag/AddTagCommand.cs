using SergioIzq.Application.Kernel.Messaging;

namespace Synap.Application.Features.Notes.Commands.AddTag;

public sealed record AddTagCommand(Guid NoteId, string TagName) : ICommand;
