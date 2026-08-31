using Synap.Shared.Application.Interfaces;
using System.Security.Cryptography;

namespace Synap.Infrastructure.Services.Auth;

/// <summary>
/// Ported from Kash's token-personal-api feature: a random opaque token, only ever persisted
/// as a SHA-256 hash. Base64Url (not standard Base64) so the plaintext token is bearer-header
/// and URL safe.
/// </summary>
public sealed class ApiTokenHasher : IApiTokenHasher
{
    public (string PlainToken, string Hash) GenerateToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var plainToken = Convert.ToBase64String(randomBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return (plainToken, Hash(plainToken));
    }

    public string Hash(string plainToken)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plainToken));
        return Convert.ToHexString(bytes);
    }
}
