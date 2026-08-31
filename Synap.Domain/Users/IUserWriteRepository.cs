using SergioIzq.Domain.Kernel.Interfaces.Repositories;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Domain;

public interface IUserWriteRepository : IWriteRepository<User, UserId>
{
}
