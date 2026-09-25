using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Notes.Queries;

/// <summary>specs/knowledge-vault "List own tags" - feeds the tag filter now that search is paged.</summary>
public sealed record ListTagsQuery : IQuery<IReadOnlyList<string>>;

public sealed class ListTagsQueryHandler : IQueryHandler<ListTagsQuery, IReadOnlyList<string>>
{
    private readonly INoteReadRepository _noteReadRepository;
    private readonly IUserContext _userContext;

    public ListTagsQueryHandler(INoteReadRepository noteReadRepository, IUserContext userContext)
    {
        _noteReadRepository = noteReadRepository;
        _userContext = userContext;
    }

    public async Task<Result<IReadOnlyList<string>>> Handle(ListTagsQuery request, CancellationToken cancellationToken)
        => Result.Success(await _noteReadRepository.ListTagsAsync(_userContext.RequireUserId(), cancellationToken));
}
