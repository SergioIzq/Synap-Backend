using SergioIzq.Domain.Kernel.Interfaces.Repositories;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Domain;

public interface IUserWriteRepository : IWriteRepository<User, UserId>
{
    /// <summary>
    /// Permanently deletes the user and everything they own (notes - and with them tags links
    /// and embeddings - tags, Groq key, API token) in one transaction (specs/identity "Delete
    /// account"). notes/tags have no FK to users, so this can't rely on a cascade.
    /// </summary>
    Task DeleteWithAllDataAsync(Guid userId, CancellationToken cancellationToken = default);
}
