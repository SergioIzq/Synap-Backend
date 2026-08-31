using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Notes.Commands.Update;

public sealed class UpdateNoteCommandHandler : ICommandHandler<UpdateNoteCommand>
{
    private readonly INoteWriteRepository _noteWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public UpdateNoteCommandHandler(INoteWriteRepository noteWriteRepository, IUnitOfWork unitOfWork, IUserContext userContext)
    {
        _noteWriteRepository = noteWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result> Handle(UpdateNoteCommand request, CancellationToken cancellationToken)
    {
        var note = await _noteWriteRepository.GetOwnedByUserAsync(request.NoteId, _userContext.RequireUserId(), cancellationToken);
        if (note is null)
        {
            return Result.Failure(Error.NotFound("Note not found."));
        }

        note.UpdateContent(request.Title, request.Content);
        _noteWriteRepository.Update(note);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
