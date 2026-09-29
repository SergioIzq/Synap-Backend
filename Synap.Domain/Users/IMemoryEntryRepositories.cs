using SergioIzq.Domain.Kernel.Interfaces.Repositories;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Domain;

public interface IMemoryEntryWriteRepository : IWriteRepository<MemoryEntry, MemoryEntryId>
{
    /// <summary>Tracked; null when the entry doesn't exist or belongs to someone else - callers can't tell which.</summary>
    Task<MemoryEntry?> GetOwnedByUserAsync(Guid entryId, Guid userId, CancellationToken cancellationToken = default);

    Task<int> CountByUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task DeleteAllByUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

public interface IMemoryEntryReadRepository
{
    /// <summary>The user's entries, most recently updated first.</summary>
    Task<IReadOnlyList<MemoryEntrySummary>> ListByUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record MemoryEntrySummary(Guid Id, string Text, DateTime UpdatedAt)
{
    public static MemoryEntrySummary From(MemoryEntry entry) => new(entry.Id.Value, entry.Text, entry.UpdatedAt);
}
