namespace Synap.Shared.Application.Interfaces;

/// <summary>
/// Short-lived cache of "does this user still exist?", consulted on every JWT-authenticated
/// request so a session token of a deleted account stops working (backend-hardening design.md
/// Decision 2) without a database hit per request.
/// </summary>
public interface IUserExistenceCache
{
    Task<bool> ExistsAsync(Guid userId, CancellationToken cancellationToken = default);

    void Invalidate(Guid userId);
}
