using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Notes.Queries;

public sealed class SearchNotesQueryHandler : IQueryHandler<SearchNotesQuery, IReadOnlyList<NoteSearchResult>>
{
    private readonly INoteReadRepository _noteReadRepository;
    private readonly IUserContext _userContext;

    public SearchNotesQueryHandler(INoteReadRepository noteReadRepository, IUserContext userContext)
    {
        _noteReadRepository = noteReadRepository;
        _userContext = userContext;
    }

    public async Task<Result<IReadOnlyList<NoteSearchResult>>> Handle(SearchNotesQuery request, CancellationToken cancellationToken)
    {
        var results = await _noteReadRepository.SearchAsync(
            _userContext.RequireUserId(), request.SearchTerm, request.Tag, cancellationToken);

        return Result.Success(results);
    }
}
