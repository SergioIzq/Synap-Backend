using Synap.Domain;

namespace Synap.Shared.Application.Interfaces;

/// <summary>
/// Thin adapter over the kernel's generic <c>KernelJwtTokenGenerator</c> (which takes raw
/// id/email) so Application handlers can pass a <see cref="User"/> directly.
/// </summary>
public interface IJwtTokenGenerator
{
    (string Token, DateTime ExpiresAt) GenerateToken(User user);
}
