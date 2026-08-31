using SergioIzq.Domain.Kernel.Interfaces.Repositories;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Domain;

public interface INoteWriteRepository : IWriteRepository<Note, NoteId>
{
    /// <summary>
    /// Fetches a note only if it belongs to the given user - filtering by ownership in the
    /// query itself (not fetch-then-check) so a note that exists but belongs to someone else
    /// looks identical to one that doesn't exist at all.
    /// </summary>
    Task<Note?> GetOwnedByUserAsync(Guid noteId, Guid userId, CancellationToken cancellationToken = default);
}
