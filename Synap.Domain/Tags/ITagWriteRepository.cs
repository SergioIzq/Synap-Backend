using SergioIzq.Domain.Kernel.Interfaces.Repositories;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Domain;

public interface ITagWriteRepository : IWriteRepository<Tag, TagId>
{
    /// <summary>
    /// Tracked lookup (not a plain read-model query) - the result is meant to be attached
    /// straight onto a Note in the same unit of work, matching Kash's FindOrCreateAsync
    /// pattern for "look up an existing row inside the write side, not a separate read model".
    /// </summary>
    Task<Tag?> GetByNameAsync(Guid userId, string name, CancellationToken cancellationToken = default);
}
