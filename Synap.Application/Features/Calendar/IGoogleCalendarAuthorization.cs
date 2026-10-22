namespace Synap.Application.Features.Calendar;

/// <summary>
/// Lo que el flujo OAuth de Google necesita del proveedor (calendar-integration design.md
/// Decisión 3). La lectura de eventos va aparte, por <see cref="ICalendarReader"/>.
/// </summary>
public interface IGoogleCalendarAuthorization
{
    /// <summary>La URL de consentimiento, con el scope de solo lectura y el <paramref name="state"/> dado.</summary>
    string BuildConsentUrl(string state);

    /// <summary>Intercambia el código; null si Google no lo acepta o no devuelve refresh token.</summary>
    Task<GoogleCalendarGrant?> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>La cuenta del calendario principal (su `summary`, que es el email); null si no se pudo leer.</summary>
    Task<string?> GetAccountAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>Pide a Google que revoque el token. False si no se pudo; nunca lanza.</summary>
    Task<bool> RevokeAsync(string token, CancellationToken cancellationToken = default);
}

public sealed record GoogleCalendarGrant(string RefreshToken, string AccessToken, DateTime AccessTokenExpiresAt);
