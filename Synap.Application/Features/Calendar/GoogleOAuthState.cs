using Synap.Shared.Application.Interfaces;
using System.Security.Cryptography;
using System.Text.Json;

namespace Synap.Application.Features.Calendar;

/// <summary>
/// El `state` del flujo OAuth (calendar-integration design.md Decisión 3): el usuario, un nonce de
/// un solo uso y la caducidad, cifrados con <see cref="ISecretProtector"/>. El cifrado impide
/// falsificarlo; el nonce guardado en el usuario impide repetirlo.
/// </summary>
public sealed class GoogleOAuthState
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ISecretProtector _secretProtector;

    public GoogleOAuthState(ISecretProtector secretProtector)
    {
        _secretProtector = secretProtector;
    }

    public static string NewNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string Issue(Guid userId, string nonce, DateTime expiresAtUtc) =>
        _secretProtector.Protect(JsonSerializer.Serialize(new Payload(userId, nonce, expiresAtUtc)));

    /// <summary>El contenido del `state`, o null si no es uno emitido aquí. Nunca lanza.</summary>
    public Payload? TryRead(string? state)
    {
        if (string.IsNullOrWhiteSpace(state) || !_secretProtector.TryUnprotect(state, out var json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Payload>(json) is { UserId: var id, Nonce.Length: > 0 } payload && id != Guid.Empty
                ? payload
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record Payload(Guid UserId, string Nonce, DateTime ExpiresAt);
}
