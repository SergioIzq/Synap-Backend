using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Notes.Queries;

/// <summary>specs/knowledge-vault "View a single note".</summary>
public sealed record GetNoteByIdQuery(Guid NoteId) : IQuery<NoteSearchResult>;

public sealed class GetNoteByIdQueryHandler : IQueryHandler<GetNoteByIdQuery, NoteSearchResult>
{
    private readonly INoteReadRepository _noteReadRepository;
    private readonly IUserContext _userContext;

    public GetNoteByIdQueryHandler(INoteReadRepository noteReadRepository, IUserContext userContext)
    {
        _noteReadRepository = noteReadRepository;
        _userContext = userContext;
    }

    public async Task<Result<NoteSearchResult>> Handle(GetNoteByIdQuery request, CancellationToken cancellationToken)
    {
        var note = await _noteReadRepository.GetByIdAsync(_userContext.RequireUserId(), request.NoteId, cancellationToken);

        // Same answer for "someone else's" and "doesn't exist" - never reveal other users' ids.
        return note is null
            ? Result.Failure<NoteSearchResult>(Error.NotFound("Nota no encontrada."))
            : Result.Success(note);
    }
}
