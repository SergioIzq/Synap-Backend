using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Data.Users;

public sealed class UserWriteRepository : AbsWriteRepository<User, UserId>, IUserWriteRepository
{
    public UserWriteRepository(SynapDbContext context) : base(context)
    {
    }
}
