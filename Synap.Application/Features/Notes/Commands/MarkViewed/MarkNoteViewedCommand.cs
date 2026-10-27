using SergioIzq.Application.Kernel.Messaging;

namespace Synap.Application.Features.Notes.Commands.MarkViewed;

/// <summary>
/// Registra que el usuario ha abierto la nota.
/// Solo mueve <c>LastViewedAt</c> — no es una edición, no encola embedding (smart-resurfacing, Decisión 4).
/// </summary>
public sealed record MarkNoteViewedCommand(Guid NoteId) : ICommand;
