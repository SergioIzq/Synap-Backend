using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.Interfaces;

namespace Synap.Application.Features.Notes.Queries;

public sealed class GetRelatedNotesQueryHandler : IQueryHandler<GetRelatedNotesQuery, IReadOnlyList<RelatedNote>>
{
    private readonly INoteWriteRepository _noteWriteRepository;
    private readonly IAiServiceClient _aiServiceClient;
    private readonly IUserContext _userContext;

    public GetRelatedNotesQueryHandler(INoteWriteRepository noteWriteRepository, IAiServiceClient aiServiceClient, IUserContext userContext)
    {
        _noteWriteRepository = noteWriteRepository;
        _aiServiceClient = aiServiceClient;
        _userContext = userContext;
    }

    public async Task<Result<IReadOnlyList<RelatedNote>>> Handle(GetRelatedNotesQuery request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();

        // Confirms ownership before asking the AI service - a note ID that exists but belongs
        // to someone else must look exactly like one that doesn't exist (per the isolation
        // invariant already applied elsewhere, e.g. INoteWriteRepository.GetOwnedByUserAsync).
        var note = await _noteWriteRepository.GetOwnedByUserAsync(request.NoteId, userId, cancellationToken);
        if (note is null)
        {
            return Result.Failure<IReadOnlyList<RelatedNote>>(Error.NotFound("Note not found."));
        }

        var related = await _aiServiceClient.GetRelatedNotesAsync(request.NoteId, userId, cancellationToken);
        return Result.Success(related);
    }
}
