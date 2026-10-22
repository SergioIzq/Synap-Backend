using Synap.Application.Features.Calendar;
using System.Collections.Concurrent;

namespace Synap.IntegrationTests;

/// <summary>
/// Google tal como lo ve la API en pruebas: nada sale del proceso. Como el fixture es compartido,
/// el comportamiento depende del código: uno que empieza por "ok-" se acepta y da una cuenta
/// `&lt;código&gt;@gmail.com`; cualquier otro se rechaza.
/// </summary>
public sealed class ApiGoogleCalendarFake : IGoogleCalendarAuthorization
{
    public ConcurrentBag<string> Exchanged { get; } = [];

    public ConcurrentBag<string> Revoked { get; } = [];

    /// <summary>Tokens cuya revocación falla, como si Google no respondiera.</summary>
    public ConcurrentDictionary<string, bool> RevocationFails { get; } = new();

    public string BuildConsentUrl(string state) =>
        $"https://accounts.google.test/o/oauth2/v2/auth?scope=calendar.events.readonly&state={Uri.EscapeDataString(state)}";

    public Task<GoogleCalendarGrant?> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        Exchanged.Add(code);
        return Task.FromResult(code.StartsWith("ok-", StringComparison.Ordinal)
            ? new GoogleCalendarGrant($"refresh-{code}", $"access-{code}", DateTime.UtcNow.AddHours(1))
            : null);
    }

    public Task<string?> GetAccountAsync(string accessToken, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(accessToken.StartsWith("access-", StringComparison.Ordinal)
            ? $"{accessToken["access-".Length..]}@gmail.com"
            : null);

    public Task<bool> RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        Revoked.Add(token);
        return Task.FromResult(!RevocationFails.ContainsKey(token));
    }
}
