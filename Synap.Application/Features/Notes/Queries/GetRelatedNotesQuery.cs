using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Notes.Queries;

public sealed record GetRelatedNotesQuery(Guid NoteId) : IQuery<IReadOnlyList<RelatedNote>>;
