using Microsoft.EntityFrameworkCore;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Data.Tags;

public sealed class TagWriteRepository : AbsWriteRepository<Tag, TagId>, ITagWriteRepository
{
    public TagWriteRepository(SynapDbContext context) : base(context)
    {
    }

    // Tracked, not AsNoTracking: the result is meant to be attached to a Note and saved in the
    // same unit of work (see ITagWriteRepository.GetByNameAsync).
    public Task<Tag?> GetByNameAsync(Guid userId, string name, CancellationToken cancellationToken = default)
    {
        var owner = UserId.CreateFromDatabase(userId);
        // Case-insensitive: "#Docker" reuses an existing "docker" (keeping its spelling) instead of
        // creating a near-duplicate tag.
        var lowered = name.ToLower();
        return Context.Set<Tag>()
            .AsTracking()
            .Where(t => t.UserId == owner && t.Name.ToLower() == lowered)
            .OrderBy(t => t.FechaCreacion)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
