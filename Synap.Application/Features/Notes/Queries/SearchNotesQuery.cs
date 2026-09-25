using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Notes.Queries;

/// <summary>Type is the wire name ("text", "codeSnippet", "bookmark"), case-insensitive.</summary>
public sealed record SearchNotesQuery(
    string? SearchTerm,
    string? Tag,
    string? Type = null,
    int Page = 1,
    int PageSize = NoteSearchCriteria.DefaultPageSize) : IQuery<PagedResult<NoteSearchResult>>;
