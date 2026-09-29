using Microsoft.EntityFrameworkCore;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Data.Memory;

public sealed class MemoryEntryWriteRepository : AbsWriteRepository<MemoryEntry, MemoryEntryId>, IMemoryEntryWriteRepository
{
    public MemoryEntryWriteRepository(SynapDbContext context) : base(context)
    {
    }

    public Task<MemoryEntry?> GetOwnedByUserAsync(Guid entryId, Guid userId, CancellationToken cancellationToken = default)
    {
        var id = MemoryEntryId.CreateFromDatabase(entryId);
        var owner = UserId.CreateFromDatabase(userId);
        return Context.Set<MemoryEntry>().AsTracking().FirstOrDefaultAsync(m => m.Id == id && m.UserId == owner, cancellationToken);
    }

    public Task<int> CountByUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var owner = UserId.CreateFromDatabase(userId);
        return Context.Set<MemoryEntry>().CountAsync(m => m.UserId == owner, cancellationToken);
    }

    public Task DeleteAllByUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var owner = UserId.CreateFromDatabase(userId);
        return Context.Set<MemoryEntry>().Where(m => m.UserId == owner).ExecuteDeleteAsync(cancellationToken);
    }
}

public sealed class MemoryEntryReadRepository : IMemoryEntryReadRepository
{
    private readonly SynapDbContext _context;

    public MemoryEntryReadRepository(SynapDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<MemoryEntrySummary>> ListByUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var owner = UserId.CreateFromDatabase(userId);
        // Mapped after loading: EF can't translate `.Value` on a value-converted key (see
        // AbsWriteRepository), and there are at most MemoryEntry.MaxEntriesPerUser rows.
        var entries = await _context.Set<MemoryEntry>()
            .AsNoTracking()
            .Where(m => m.UserId == owner)
            .OrderByDescending(m => m.UpdatedAt)
            .ThenByDescending(m => m.FechaCreacion)
            .ToListAsync(cancellationToken);
        return entries.Select(MemoryEntrySummary.From).ToList();
    }
}
