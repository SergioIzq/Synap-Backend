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
        => Context.Set<Tag>().AsTracking().FirstOrDefaultAsync(t => t.UserId.Value == userId && t.Name == name, cancellationToken);
}
