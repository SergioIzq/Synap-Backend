using SergioIzq.AspNetCore.Kernel.Auth;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Services.Auth;

/// <summary>
/// Thin adapter of <see cref="IJwtTokenGenerator"/> (which receives Synap's User entity) over
/// the kernel's generic token generator.
/// </summary>
public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly KernelJwtTokenGenerator _kernelGenerator;

    public JwtTokenGenerator(KernelJwtTokenGenerator kernelGenerator)
    {
        _kernelGenerator = kernelGenerator;
    }

    public (string Token, DateTime ExpiresAt) GenerateToken(User user)
        => _kernelGenerator.GenerateToken(user.Id.Value, user.Email.Value);
}
