using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Notes.Commands.SetStatus;

/// <summary>
/// Sets, replaces or clears a note's status. Deliberately does NOT enqueue an embedding refresh
/// the way UpdateNoteCommandHandler does: the note's text has not changed, so its embedding is
/// still correct, and regenerating it would cost an AI call per click.
/// </summary>
public sealed class SetNoteStatusCommandHandler : ICommandHandler<SetNoteStatusCommand>
{
    public static readonly Error InvalidStatus = Error.Validation(
        $"El estado de la nota debe ser uno de: {string.Join(", ", NoteStatusWire.AcceptedValues)}.");

    private readonly INoteWriteRepository _noteWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public SetNoteStatusCommandHandler(
        INoteWriteRepository noteWriteRepository,
        IUnitOfWork unitOfWork,
        IUserContext userContext)
    {
        _noteWriteRepository = noteWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result> Handle(SetNoteStatusCommand request, CancellationToken cancellationToken)
    {
        if (!NoteStatusWire.TryParse(request.Status, out var status))
        {
            return Result.Failure(InvalidStatus);
        }

        var userId = _userContext.RequireUserId();

        // Same answer for "someone else's" and "doesn't exist" - never reveal other users' notes
        // (specs/knowledge-vault "Another user's note cannot be marked").
        var note = await _noteWriteRepository.GetOwnedByUserAsync(request.NoteId, userId, cancellationToken);
        if (note is null)
        {
            return Result.Failure(Error.NotFound("Nota no encontrada."));
        }

        note.SetStatus(status);
        _noteWriteRepository.Update(note);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
