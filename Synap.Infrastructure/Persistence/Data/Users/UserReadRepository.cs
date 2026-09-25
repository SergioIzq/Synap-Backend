using Microsoft.EntityFrameworkCore;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Data.Users;

/// <summary>
/// Uses EF Core (no-tracking), not Dapper: unlike Kash's read repositories - which return
/// plain DTOs for listings/reports - identity lookups need to reconstruct the full User
/// aggregate (its private-constructor-guarded PasswordHash) to verify a login. Dapper stays
/// reserved for genuine DTO/reporting-style reads (e.g. notes search, task 3.6).
/// </summary>
public sealed class UserReadRepository : IUserReadRepository
{
    private readonly SynapDbContext _context;

    public UserReadRepository(SynapDbContext context)
    {
        _context = context;
    }

    public Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken = default)
        => _context.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    public Task<string?> GetSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var id = UserId.CreateFromDatabase(userId);
        return _context.Set<User>().AsNoTracking().Where(u => u.Id == id).Select(u => (string?)u.SecurityStamp).FirstOrDefaultAsync(cancellationToken);
    }

    public Task<User?> GetByPasswordResetTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
        => _context.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.PasswordResetTokenHash == tokenHash, cancellationToken);

    public Task<User?> GetByApiTokenHashAsync(string apiTokenHash, CancellationToken cancellationToken = default)
        => _context.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.ApiTokenHash == apiTokenHash, cancellationToken);
}
