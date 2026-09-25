using Microsoft.EntityFrameworkCore;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Data.Users;

public sealed class UserWriteRepository : AbsWriteRepository<User, UserId>, IUserWriteRepository
{
    public UserWriteRepository(SynapDbContext context) : base(context)
    {
    }

    public async Task DeleteWithAllDataAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Raw SQL in a transaction: note_tags and note_embeddings go with their notes (ON DELETE
        // CASCADE); notes and tags only carry user_id, so they are deleted explicitly.
        // EnableRetryOnFailure requires user transactions to run through the execution strategy.
        var strategy = Context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await Context.Database.BeginTransactionAsync(cancellationToken);

            await Context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notes WHERE user_id = {userId}", cancellationToken);
            await Context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM tags WHERE user_id = {userId}", cancellationToken);
            await Context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM users WHERE id = {userId}", cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });

        // Anything this context was tracking for the user is gone now.
        Context.ChangeTracker.Clear();
    }
}
