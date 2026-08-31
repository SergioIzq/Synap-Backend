using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.BackgroundJobs;

namespace Synap.Application.Features.Notes.Commands.QuickCapture;

public sealed class QuickCaptureCommandHandler : ICommandHandler<QuickCaptureCommand, Guid>
{
    private readonly INoteWriteRepository _noteWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly IBackgroundJobQueue _backgroundJobQueue;

    public QuickCaptureCommandHandler(
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

    public async Task<Result<Guid>> Handle(QuickCaptureCommand request, CancellationToken cancellationToken)
    {
        var type = request.Type ?? NoteTypeInference.Infer(request.Content);

        var note = await NoteCreationSupport.CreateAndPersistAsync(
            _noteWriteRepository,
            _unitOfWork,
            _backgroundJobQueue,
            _userContext.RequireUserId(),
            type,
            title: null,
            request.Content,
            cancellationToken);

        return Result.Success(note.Id.Value);
    }
}
