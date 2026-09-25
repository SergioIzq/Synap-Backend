using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Notes.Commands.Create;

/// <summary>
/// specs/knowledge-vault "Capture a note": title and tags in the same operation; a missing Type
/// is inferred from the content (a lone URL becomes a bookmark).
/// </summary>
public sealed record CreateNoteCommand(NoteType? Type, string? Title, string Content, IReadOnlyList<string>? Tags = null) : ICommand<Guid>;
