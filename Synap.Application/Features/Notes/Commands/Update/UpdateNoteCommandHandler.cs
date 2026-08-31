using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Notes;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.BackgroundJobs;

namespace Synap.Application.Features.Notes.Commands.Update;

public sealed class UpdateNoteCommandHandler : ICommandHandler<UpdateNoteCommand>
{
    private readonly INoteWriteRepository _noteWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly IBackgroundJobQueue _backgroundJobQueue;

    public UpdateNoteCommandHandler(
        INoteWriteRepository noteWriteRepository,
        IUnitOfWork unitOfWork,
        IUserContext userContext,
        IBackgroundJobQueue backgroundJobQueue)
    {
        _noteWriteRepository = noteWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
        _backgroundJobQueue = backgroundJobQueue;
    }

    public async Task<Result> Handle(UpdateNoteCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var note = await _noteWriteRepository.GetOwnedByUserAsync(request.NoteId, userId, cancellationToken);
        if (note is null)
        {
            return Result.Failure(Error.NotFound("Note not found."));
        }

        note.UpdateContent(request.Title, request.Content);
        _noteWriteRepository.Update(note);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // specs/ai-assistant "Embedding refreshed on edit".
        EmbeddingSupport.EnqueueGeneration(_backgroundJobQueue, note.Id.Value, userId, request.Content);

        return Result.Success();
    }
}
