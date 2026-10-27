using Microsoft.EntityFrameworkCore;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Data.Notes;

public sealed class NoteWriteRepository : AbsWriteRepository<Note, NoteId>, INoteWriteRepository
{
    public NoteWriteRepository(SynapDbContext context) : base(context)
    {
    }

    public Task<Note?> GetOwnedByUserAsync(Guid noteId, Guid userId, CancellationToken cancellationToken = default)
    {
        var id = NoteId.CreateFromDatabase(noteId);
        var owner = UserId.CreateFromDatabase(userId);
        return Context.Set<Note>()
            .AsTracking()
            .Include(n => n.Tags)
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == owner, cancellationToken);
    }

    public async Task MarkResurfacedAsync(
        Guid userId, IReadOnlyList<Guid> noteIds, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (noteIds.Count == 0)
        {
            return;
        }

        var noteIdsArray = noteIds.Select(id => NoteId.CreateFromDatabase(id)).ToArray();
        var owner = UserId.CreateFromDatabase(userId);

        await Context.Set<Note>()
            .Where(n => n.UserId == owner && noteIdsArray.Contains(n.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastResurfacedAt, nowUtc), cancellationToken);
    }
}
