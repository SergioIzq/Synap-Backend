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
        => Context.Set<Note>()
            .AsTracking()
            .Include(n => n.Tags)
            .FirstOrDefaultAsync(n => n.Id.Value == noteId && n.UserId.Value == userId, cancellationToken);
}
