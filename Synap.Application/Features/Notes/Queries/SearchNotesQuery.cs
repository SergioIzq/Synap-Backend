using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Notes.Queries;

/// <summary>
/// Type is the wire name ("text", "codeSnippet", "bookmark"), case-insensitive. Status is a
/// comma-separated list of wire names plus "none" for notes carrying no status at all
/// ("pending,inProgress", "none", "completed"); blank means the default filter - everything except
/// completed, unmarked notes included (note-status design.md Decision 4).
/// </summary>
public sealed record SearchNotesQuery(
    string? SearchTerm,
    string? Tag,
    string? Type = null,
    int Page = 1,
    int PageSize = NoteSearchCriteria.DefaultPageSize,
    string? Status = null) : IQuery<PagedResult<NoteSearchResult>>;
