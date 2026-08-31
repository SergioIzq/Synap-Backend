using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Notes.Commands.QuickCapture;

/// <summary>
/// Used by the iOS Shortcut (specs/knowledge-vault "Quick capture from an external trigger")
/// and any other non-interactive caller: the type is inferred when not given, since a Shortcut
/// invoked from the share sheet only ever hands over raw text or a URL.
/// </summary>
public sealed record QuickCaptureCommand(string Content, NoteType? Type) : ICommand<Guid>;
