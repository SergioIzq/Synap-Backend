namespace Synap.Shared.Application.Interfaces;

/// <summary>
/// Short-lived cache of each user's current security stamp, consulted on every JWT-authenticated
/// request (password-recovery design.md Decision 4): a session is only valid while its "stamp"
/// claim matches. Null means the user no longer exists (deleted account). Invalidate whenever
/// the stamp changes or the user is deleted, so the change takes effect immediately.
/// </summary>
public interface IUserSessionCache
{
    Task<string?> GetSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default);

    void Invalidate(Guid userId);
}
