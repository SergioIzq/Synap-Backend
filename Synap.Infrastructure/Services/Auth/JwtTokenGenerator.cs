using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Synap.Infrastructure.Services.Auth;

/// <summary>
/// Issues session JWTs itself instead of delegating to the kernel's KernelJwtTokenGenerator,
/// which can't add claims: every token carries the user's security stamp so a password change
/// ends older sessions (password-recovery design.md Decision 4). Same JwtSettings section,
/// algorithm and base claims as the kernel's tokens, so the kernel's JwtBearer validation and
/// AbsController.GetCurrentUserId() keep working unchanged.
/// </summary>
public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    public const string SecurityStampClaim = "stamp";

    /// <summary>256 bits for HS256 - shorter keys only fail when the first token is signed.</summary>
    public const int MinSecretKeyLength = 32;

    private readonly SigningCredentials _signingCredentials;
    private readonly string? _issuer;
    private readonly string? _audience;
    private readonly TimeSpan _lifetime;

    public JwtTokenGenerator(IConfiguration configuration)
    {
        var section = configuration.GetSection("JwtSettings");
        var secretKey = section["SecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey) || Encoding.UTF8.GetByteCount(secretKey) < MinSecretKeyLength)
        {
            throw new InvalidOperationException(
                $"'JwtSettings:SecretKey' (JWT_SECRET_KEY) must be at least {MinSecretKeyLength} characters - generate one with `openssl rand -base64 48`.");
        }

        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)), SecurityAlgorithms.HmacSha256);
        _issuer = section["Issuer"];
        _audience = section["Audience"];
        _lifetime = TimeSpan.FromMinutes(section.GetValue("ExpirationMinutes", 720));
    }

    public (string Token, DateTime ExpiresAt) GenerateToken(User user)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.Add(_lifetime);
        var userId = user.Id.Value.ToString();

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Email, user.Email.Value),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(SecurityStampClaim, user.SecurityStamp),
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: _signingCredentials);

        // JwtSecurityToken sets "iat" only through the handler's descriptor path - add it here.
        token.Payload[JwtRegisteredClaimNames.Iat] = EpochTime.GetIntDate(now);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
