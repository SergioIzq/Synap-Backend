using Synap.Shared.Domain.ValueObjects;

namespace Synap.Domain;

public interface IUserReadRepository
{
    Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken = default);
    Task<User?> GetByApiTokenHashAsync(string apiTokenHash, CancellationToken cancellationToken = default);
}
