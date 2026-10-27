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

    /// <summary>
    /// Marca <c>last_resurfaced_at = now</c> en las notas indicadas que pertenecen al usuario,
    /// en una sola consulta. Si algún id no pertenece al usuario, se ignora (nunca se lanza error).
    /// Se llama solo tras la entrega exitosa del briefing (smart-resurfacing, Decisión 6).
    /// </summary>
    Task MarkResurfacedAsync(Guid userId, IReadOnlyList<Guid> noteIds, DateTime nowUtc, CancellationToken cancellationToken = default);
}
