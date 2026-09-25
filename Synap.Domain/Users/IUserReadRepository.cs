using Synap.Shared.Domain.ValueObjects;

namespace Synap.Domain;

public interface IUserReadRepository
{
    Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken = default);
    Task<User?> GetByApiTokenHashAsync(string apiTokenHash, CancellationToken cancellationToken = default);
    /// <summary>Null when the user doesn't exist.</summary>
    Task<string?> GetSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<User?> GetByPasswordResetTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);
}
