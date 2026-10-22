using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Lee Google Calendar con <see cref="HttpClient"/> directo, como Telegram y Groq
/// (calendar-integration design.md Decisión 3): `events.list` sobre el calendario de la conexión
/// con las recurrencias expandidas, y el access token renovado bajo demanda.
///
/// `invalid_grant` al renovar significa que el usuario revocó el acceso o que el refresh token
/// caducó: se devuelve <see cref="CalendarReadResult.NeedsReconnect"/>. Cualquier otro fallo es
/// temporal y no marca la conexión.
/// </summary>
public sealed class GoogleCalendarClient : ICalendarProviderClient, IGoogleCalendarAuthorization
{
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string CalendarApi = "https://www.googleapis.com/calendar/v3/";
    public const string ConsentEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary>El mínimo que permite `events.list` sobre `primary` (design.md Decisión 3).</summary>
    public const string Scope = "https://www.googleapis.com/auth/calendar.events.readonly";

    /// <summary>Margen con el que se renueva un access token que está a punto de caducar.</summary>
    private static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(1);

    /// <summary>El máximo que admite `events.list` por página; nunca se piden más.</summary>
    private const int MaxResultsPerPage = 250;

    private readonly HttpClient _httpClient;
    private readonly GoogleCalendarOptions _options;
    private readonly ILogger<GoogleCalendarClient> _logger;

    public GoogleCalendarClient(
        HttpClient httpClient, IOptions<GoogleCalendarOptions> options, ILogger<GoogleCalendarClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public CalendarProvider Provider => CalendarProvider.Google;

    public async Task<CalendarProviderRead> ReadAsync(CalendarProviderRequest request, CancellationToken cancellationToken)
    {
        if (request.Credentials is not CalendarCredentials.Google credentials)
        {
            return new CalendarProviderRead(new CalendarReadResult.Unavailable("credenciales de otro proveedor"));
        }

        if (!_options.IsAvailable)
        {
            // Un despliegue que quitó el cliente OAuth no puede leer lo conectado con él
            // (design.md Migration Plan, paso 4): se trata como fallo temporal, no como revocación.
            return new CalendarProviderRead(new CalendarReadResult.Unavailable("Google no configurado"));
        }

        try
        {
            CalendarCredentials.Google? renewed = null;

            if (NeedsRenewal(credentials))
            {
                var refreshed = await RefreshAsync(credentials, cancellationToken);
                if (refreshed.Result is not null)
                {
                    return new CalendarProviderRead(refreshed.Result);
                }

                credentials = renewed = refreshed.Credentials!;
            }

            using var response = await SendListAsync(request, credentials.AccessToken!, cancellationToken);

            // Un 401 con un token que creíamos vigente: se renueva una vez y se reintenta.
            if (response.StatusCode == HttpStatusCode.Unauthorized && renewed is null)
            {
                var refreshed = await RefreshAsync(credentials, cancellationToken);
                if (refreshed.Result is not null)
                {
                    return new CalendarProviderRead(refreshed.Result);
                }

                credentials = renewed = refreshed.Credentials!;
                using var retried = await SendListAsync(request, credentials.AccessToken!, cancellationToken);
                return new CalendarProviderRead(await ReadEventsAsync(retried, request, cancellationToken), renewed);
            }

            return new CalendarProviderRead(await ReadEventsAsync(response, request, cancellationToken), renewed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CalendarProviderRead(new CalendarReadResult.Unavailable("Google no respondió a tiempo"));
        }
        catch (HttpRequestException)
        {
            return new CalendarProviderRead(new CalendarReadResult.Unavailable("Google inalcanzable"));
        }
        catch (JsonException)
        {
            return new CalendarProviderRead(new CalendarReadResult.Unavailable("respuesta de Google no válida"));
        }
    }

    // ---- Autorización (design.md Decisión 3) ----

    public string BuildConsentUrl(string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            // Sin estos dos, una segunda autorización no trae refresh token.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state,
        };

        return $"{ConsentEndpoint}?{string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"))}";
    }

    public async Task<GoogleCalendarGrant?> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["redirect_uri"] = _options.RedirectUri,
            });

            using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken);
            using var json = await ReadJsonAsync(response, cancellationToken);

            if (!response.IsSuccessStatusCode || json is null)
            {
                _logger.LogWarning("Google rechazó el intercambio del código: HTTP {StatusCode}", (int)response.StatusCode);
                return null;
            }

            var root = json.RootElement;
            if (Text(root, "refresh_token") is not { Length: > 0 } refreshToken
                || Text(root, "access_token") is not { Length: > 0 } accessToken)
            {
                _logger.LogWarning("Google devolvió el intercambio del código sin refresh token");
                return null;
            }

            var expiresIn = root.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out var s) ? s : 3600;
            return new GoogleCalendarGrant(refreshToken, accessToken, DateTime.UtcNow.AddSeconds(expiresIn));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("No se pudo intercambiar el código con Google: {Error}", exception.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// La cuenta es el `summary` del calendario principal, que `events.list` devuelve con los
    /// eventos: así basta con el scope de solo lectura de eventos.
    /// </summary>
    public async Task<string?> GetAccountAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Get, $"{CalendarApi}calendars/primary/events?maxResults=1&fields=summary");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(message, cancellationToken);
            using var json = await ReadJsonAsync(response, cancellationToken);

            return response.IsSuccessStatusCode && json is not null && Text(json.RootElement, "summary") is { Length: > 0 } summary
                ? summary.Trim()
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("No se pudo leer la cuenta de Google: {Error}", exception.GetType().Name);
            return null;
        }
    }

    public async Task<bool> RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var response = await _httpClient.PostAsync(RevokeEndpoint, content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    private static bool NeedsRenewal(CalendarCredentials.Google credentials) =>
        string.IsNullOrEmpty(credentials.AccessToken)
        || credentials.AccessTokenExpiresAt is not { } expiresAt
        || expiresAt - DateTime.UtcNow < RenewMargin;

    /// <summary>
    /// Renueva el access token. Devuelve las credenciales nuevas, o el resultado con el que hay que
    /// terminar la lectura si no se pudo.
    /// </summary>
    private async Task<(CalendarCredentials.Google? Credentials, CalendarReadResult? Result)> RefreshAsync(
        CalendarCredentials.Google credentials, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["refresh_token"] = credentials.RefreshToken,
        });

        using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = json?.RootElement.TryGetProperty("error", out var e) == true ? e.GetString() : null;
            if (error == "invalid_grant")
            {
                _logger.LogWarning("Google rechazó la renovación del token: invalid_grant");
                return (null, new CalendarReadResult.NeedsReconnect());
            }

            return (null, new CalendarReadResult.Unavailable($"renovación del token: HTTP {(int)response.StatusCode}"));
        }

        if (json is null
            || !json.RootElement.TryGetProperty("access_token", out var token)
            || token.GetString() is not { Length: > 0 } accessToken)
        {
            return (null, new CalendarReadResult.Unavailable("renovación del token sin access_token"));
        }

        var expiresIn = json.RootElement.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out var s)
            ? s
            : 3600;

        return (credentials with { AccessToken = accessToken, AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn) }, null);
    }

    private Task<HttpResponseMessage> SendListAsync(
        CalendarProviderRequest request, string accessToken, CancellationToken cancellationToken)
    {
        var query = string.Join('&', new[]
        {
            $"timeMin={Uri.EscapeDataString(Rfc3339(request.FromUtc))}",
            $"timeMax={Uri.EscapeDataString(Rfc3339(request.ToUtc))}",
            "singleEvents=true",
            "orderBy=startTime",
            $"maxResults={Math.Clamp(request.MaxEvents, 1, MaxResultsPerPage)}",
        });

        var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"{CalendarApi}calendars/{Uri.EscapeDataString(request.CalendarRef)}/events?{query}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return _httpClient.SendAsync(message, cancellationToken);
    }

    private static async Task<CalendarReadResult> ReadEventsAsync(
        HttpResponseMessage response, CalendarProviderRequest request, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Ni con un token recién renovado: el acceso ya no vale.
            return new CalendarReadResult.NeedsReconnect();
        }

        if (!response.IsSuccessStatusCode)
        {
            return new CalendarReadResult.Unavailable($"events.list: HTTP {(int)response.StatusCode}");
        }

        using var json = await ReadJsonAsync(response, cancellationToken)
            ?? throw new JsonException("events.list sin cuerpo");

        var events = new List<CalendarEvent>();
        if (json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (Map(item) is { } mapped)
                {
                    events.Add(mapped);
                }
            }
        }

        return new CalendarReadResult.Ok(events.Take(request.MaxEvents).ToList());
    }

    /// <summary>
    /// Un evento cancelado, uno que el usuario rechazó o uno sin inicio no se devuelven
    /// (specs/calendar-integration "Evento rechazado o cancelado").
    /// </summary>
    internal static CalendarEvent? Map(JsonElement item)
    {
        if (Text(item, "status") == "cancelled" || DeclinedBySelf(item))
        {
            return null;
        }

        if (!item.TryGetProperty("start", out var start))
        {
            return null;
        }

        var title = Text(item, "summary") is { Length: > 0 } summary ? summary.Trim() : "(sin título)";
        var location = Text(item, "location");
        var description = Text(item, "description");

        // Todo el día: Google da `date` (fin exclusivo) en lugar de `dateTime`.
        if (Text(start, "date") is { } startDate
            && DateOnly.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var days = item.TryGetProperty("end", out var endAllDay)
                && Text(endAllDay, "date") is { } endDate
                && DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                    ? end.DayNumber - day.DayNumber
                    : 1;

            return CalendarEvent.AllDay(title, day, days, location, description);
        }

        if (Text(start, "dateTime") is not { } startText
            || !DateTimeOffset.TryParse(startText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startAt))
        {
            return null;
        }

        var endAt = item.TryGetProperty("end", out var endTimed)
            && Text(endTimed, "dateTime") is { } endText
            && DateTimeOffset.TryParse(endText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
                ? parsedEnd
                : startAt;

        return new CalendarEvent(title, startAt.UtcDateTime, endAt.UtcDateTime, null, location, description);
    }

    private static bool DeclinedBySelf(JsonElement item) =>
        item.TryGetProperty("attendees", out var attendees)
        && attendees.ValueKind == JsonValueKind.Array
        && attendees.EnumerateArray().Any(a =>
            a.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True
            && Text(a, "responseStatus") == "declined");

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Rfc3339(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            // Una página de error HTML no es un fallo distinto del código de estado.
            return null;
        }
    }
}
