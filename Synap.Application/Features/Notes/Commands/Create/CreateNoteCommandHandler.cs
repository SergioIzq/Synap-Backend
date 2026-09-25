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
    private readonly ITagWriteRepository _tagWriteRepository;

    public CreateNoteCommandHandler(
        INoteWriteRepository noteWriteRepository,
        ITagWriteRepository tagWriteRepository,
        IUnitOfWork unitOfWork,
        IUserContext userContext,
        IBackgroundJobQueue backgroundJobQueue)
    {
        _noteWriteRepository = noteWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
        _backgroundJobQueue = backgroundJobQueue;
        _tagWriteRepository = tagWriteRepository;
    }

    public async Task<Result<Guid>> Handle(CreateNoteCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        // Validate every tag before touching anything: a bad tag rejects the whole note.
        var tagNames = TagAssignment.Normalize(request.Tags);
        if (tagNames.IsFailure)
        {
            return Result.Failure<Guid>(tagNames.Error);
        }

        var tags = new List<Tag>();
        foreach (var name in tagNames.Value)
        {
            tags.Add(await TagAssignment.GetOrCreateAsync(_tagWriteRepository, userId, name, cancellationToken));
        }

        // Tags, note and note_tags are saved by the same SaveChanges - all or nothing.
        var note = await NoteCreationSupport.CreateAndPersistAsync(
            _noteWriteRepository,
            _unitOfWork,
            _backgroundJobQueue,
            userId,
            request.Type ?? NoteTypeInference.Infer(request.Content),
            string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim(),
            request.Content,
            cancellationToken,
            tags);

        return Result.Success(note.Id.Value);
    }
}
