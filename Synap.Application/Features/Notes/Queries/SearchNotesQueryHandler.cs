using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using Synap.Domain;
using Synap.Shared.Application;

namespace Synap.Application.Features.Notes.Queries;

public sealed class SearchNotesQueryHandler : IQueryHandler<SearchNotesQuery, PagedResult<NoteSearchResult>>
{
    public static readonly Error InvalidPage = Error.Validation("La página debe ser 1 o mayor.");
    public static readonly Error InvalidPageSize =
        Error.Validation($"El tamaño de página debe estar entre 1 y {NoteSearchCriteria.MaxPageSize}.");
    public static readonly Error InvalidType = Error.Validation("El tipo de nota debe ser text, codeSnippet o bookmark.");
    public static readonly Error InvalidStatus = Error.Validation(
        $"El estado de la nota debe ser uno de: {string.Join(", ", NoteStatusFilter.AcceptedValues)}.");

    private readonly INoteReadRepository _noteReadRepository;
    private readonly IUserContext _userContext;

    public SearchNotesQueryHandler(INoteReadRepository noteReadRepository, IUserContext userContext)
    {
        _noteReadRepository = noteReadRepository;
        _userContext = userContext;
    }

    public async Task<Result<PagedResult<NoteSearchResult>>> Handle(SearchNotesQuery request, CancellationToken cancellationToken)
    {
        if (request.Page < 1)
        {
            return Result.Failure<PagedResult<NoteSearchResult>>(InvalidPage);
        }

        if (request.PageSize is < 1 or > NoteSearchCriteria.MaxPageSize)
        {
            return Result.Failure<PagedResult<NoteSearchResult>>(InvalidPageSize);
        }

        NoteType? type = null;
        if (!string.IsNullOrWhiteSpace(request.Type))
        {
            // Names only - Enum.TryParse would also accept numbers like "7".
            if (!Enum.GetNames<NoteType>().Contains(request.Type, StringComparer.OrdinalIgnoreCase))
            {
                return Result.Failure<PagedResult<NoteSearchResult>>(InvalidType);
            }

            type = Enum.Parse<NoteType>(request.Type, ignoreCase: true);
        }

        // "none" is a value of the filter, not a status: it asks for the notes that are not work
        // at all (design.md Decision 4). An unknown value fails the whole request rather than
        // being dropped, so a typo never silently returns the wrong notes.
        if (!NoteStatusFilter.TryParse(request.Status, out var statusFilter))
        {
            return Result.Failure<PagedResult<NoteSearchResult>>(InvalidStatus);
        }

        var criteria = new NoteSearchCriteria(
            string.IsNullOrWhiteSpace(request.SearchTerm) ? null : request.SearchTerm.Trim(),
            string.IsNullOrWhiteSpace(request.Tag) ? null : request.Tag.Trim(),
            type,
            request.Page,
            request.PageSize,
            statusFilter);

        var results = await _noteReadRepository.SearchAsync(_userContext.RequireUserId(), criteria, cancellationToken);

        return Result.Success(results);
    }
}
