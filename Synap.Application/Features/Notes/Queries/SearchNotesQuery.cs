using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Notes.Queries;

public sealed record SearchNotesQuery(string? SearchTerm, string? Tag) : IQuery<IReadOnlyList<NoteSearchResult>>;
