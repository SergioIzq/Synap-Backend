using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Notes.Commands.MarkViewed;

public sealed class MarkNoteViewedCommandHandler : ICommandHandler<MarkNoteViewedCommand>
{
    private readonly INoteWriteRepository _noteWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public MarkNoteViewedCommandHandler(
        INoteWriteRepository noteWriteRepository,
        IUnitOfWork unitOfWork,
        IUserContext userContext)
    {
        _noteWriteRepository = noteWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result> Handle(MarkNoteViewedCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var note = await _noteWriteRepository.GetOwnedByUserAsync(request.NoteId, userId, cancellationToken);
        if (note is null)
        {
            return Result.Failure(Error.NotFound("Nota no encontrada."));
        }

        note.MarkViewed();
        _noteWriteRepository.Update(note);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
