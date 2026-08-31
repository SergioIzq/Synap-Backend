using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.BackgroundJobs;

namespace Synap.Application.Features.Notes.Commands.Create;

public sealed class CreateNoteCommandHandler : ICommandHandler<CreateNoteCommand, Guid>
{
    private readonly INoteWriteRepository _noteWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly IBackgroundJobQueue _backgroundJobQueue;

    public CreateNoteCommandHandler(
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

    public async Task<Result<Guid>> Handle(CreateNoteCommand request, CancellationToken cancellationToken)
    {
        var note = await NoteCreationSupport.CreateAndPersistAsync(
            _noteWriteRepository,
            _unitOfWork,
            _backgroundJobQueue,
            _userContext.RequireUserId(),
            request.Type,
            request.Title,
            request.Content,
            cancellationToken);

        return Result.Success(note.Id.Value);
    }
}
