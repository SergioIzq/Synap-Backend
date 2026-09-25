using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Application.Features.Notes.Commands.AddTag;

public sealed class AddTagCommandHandler : ICommandHandler<AddTagCommand>
{
    private readonly INoteWriteRepository _noteWriteRepository;
    private readonly ITagWriteRepository _tagWriteRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public AddTagCommandHandler(
        INoteWriteRepository noteWriteRepository,
        ITagWriteRepository tagWriteRepository,
        IUnitOfWork unitOfWork,
        IUserContext userContext)
    {
        _noteWriteRepository = noteWriteRepository;
        _tagWriteRepository = tagWriteRepository;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result> Handle(AddTagCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        var note = await _noteWriteRepository.GetOwnedByUserAsync(request.NoteId, userId, cancellationToken);
        if (note is null)
        {
            return Result.Failure(Error.NotFound("Nota no encontrada."));
        }

        var names = TagAssignment.Normalize([request.TagName]);
        if (names.IsFailure)
        {
            return Result.Failure(names.Error);
        }

        // Reuse the same tag across notes rather than creating a duplicate (specs/knowledge-vault "Tagging").
        var tag = await TagAssignment.GetOrCreateAsync(_tagWriteRepository, userId, names.Value[0], cancellationToken);

        note.AddTag(tag);
        _noteWriteRepository.Update(note);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
