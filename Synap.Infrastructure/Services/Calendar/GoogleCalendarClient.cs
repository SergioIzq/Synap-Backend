using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Lee el calendario `primary` de Google con `HttpClient` directo, sin el SDK (calendar-integration
/// design.md Decisión 3). Renueva el access token cuando le queda menos de un minuto y devuelve el
/// nuevo para que <see cref="CalendarReader"/> lo guarde cifrado. Un `invalid_grant` o un 401 tras
/// renovar significa que el acceso ya no vale.
/// </summary>
public sealed class GoogleCalendarClient : ICalendarProviderClient
{
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string EventsEndpoint = "https://www.googleapis.com/calendar/v3/calendars/";

    private static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(1);

    private readonly HttpClient _httpClient;
    private readonly GoogleCalendarOptions _options;
    private readonly ILogger<GoogleCalendarClient> _logger;

    public GoogleCalendarClient(HttpClient httpClient, IOptions<GoogleCalendarOptions> options, ILogger<GoogleCalendarClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public CalendarProvider Provider => CalendarProvider.Google;

    public async Task<ProviderReadResult> ListEventsAsync(
        string calendarRef, string credentials, DateTime fromUtc, DateTime toUtc, int maxEvents, CancellationToken cancellationToken)
    {
        var stored = GoogleCredentials.Parse(credentials);
        if (stored is null)
        {
            return new ProviderReadResult.Rejected("credenciales de Google con formato desconocido");
        }

        string? renewed = null;
        try
        {
            if (stored.AccessToken is null || stored.AccessTokenExpiresAt - RenewMargin <= DateTime.UtcNow)
            {
                var refresh = await RefreshAsync(stored, cancellationToken);
                if (refresh.Credentials is null)
                {
                    return refresh.Failure!;
                }

                stored = refresh.Credentials;
                renewed = stored.Serialize();
            }

            var url = EventsUrl(calendarRef, fromUtc, toUtc, maxEvents);
            using var response = await GetAsync(url, stored.AccessToken!, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ProviderReadResult.Rejected($"Google respondió {(int)response.StatusCode} al leer eventos");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProviderReadResult.Unavailable($"Google respondió {(int)response.StatusCode} al leer eventos");
            }

            var body = await response.Content.ReadFromJsonAsync<EventsResponse>(cancellationToken);
            return new ProviderReadResult.Ok(ToEvents(body?.Items ?? []), renewed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new ProviderReadResult.Unavailable($"Google inalcanzable ({ex.GetType().Name})");
        }
        catch (JsonException)
        {
            return new ProviderReadResult.Unavailable("respuesta de Google ilegible");
        }
    }

    internal static string EventsUrl(string calendarRef, DateTime fromUtc, DateTime toUtc, int maxEvents)
        => $"{EventsEndpoint}{Uri.EscapeDataString(calendarRef)}/events"
            + $"?timeMin={Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}"
            + $"&timeMax={Uri.EscapeDataString(toUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}"
            + $"&singleEvents=true&orderBy=startTime&maxResults={maxEvents}"
            + "&fields=summary,items(status,summary,location,description,start,end,attendees(self,responseStatus))";

    /// <summary>Descarta los cancelados y aquellos a los que el usuario dijo que no (specs/calendar-integration).</summary>
    internal static IReadOnlyList<CalendarEvent> ToEvents(IEnumerable<GoogleEvent> items)
        => items
            .Where(i => i.Status != "cancelled")
            .Where(i => i.Attendees?.Any(a => a.Self == true && a.ResponseStatus == "declined") != true)
            .Select(ToEvent)
            .OfType<CalendarEvent>()
            .ToList();

    private static CalendarEvent? ToEvent(GoogleEvent item)
    {
        var title = string.IsNullOrWhiteSpace(item.Summary) ? "(sin título)" : item.Summary.Trim();

        // Todo el día: `date` sin hora y un fin exclusivo. Se guarda la fecha tal cual para que no
        // se mueva con la zona horaria del usuario.
        if (item.Start?.Date is { } startDate && DateOnly.TryParse(startDate, CultureInfo.InvariantCulture, out var day))
        {
            var endDay = item.End?.Date is { } endDate && DateOnly.TryParse(endDate, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : day.AddDays(1);
            return new CalendarEvent(
                title, day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), endDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                day, Blank(item.Location), Blank(item.Description));
        }

        if (item.Start?.DateTime is { } start)
        {
            var end = item.End?.DateTime ?? start;
            return new CalendarEvent(title, start.UtcDateTime, end.UtcDateTime, null, Blank(item.Location), Blank(item.Description));
        }

        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<HttpResponseMessage> GetAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<(GoogleCredentials? Credentials, ProviderReadResult? Failure)> RefreshAsync(
        GoogleCredentials stored, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = stored.RefreshToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        }), cancellationToken);

        var token = await ReadTokenAsync(response, cancellationToken);

        if (response.IsSuccessStatusCode && token?.AccessToken is { Length: > 0 } accessToken)
        {
            return (stored with
            {
                AccessToken = accessToken,
                AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn ?? 3600),
            }, null);
        }

        // `invalid_grant`: el usuario revocó el acceso, o el refresh token caducó (los 7 días del
        // modo "Testing" de Google). `invalid_client` es del despliegue, no del usuario.
        if (token?.Error == "invalid_grant")
        {
            return (null, new ProviderReadResult.Rejected("Google rechazó el refresh token (invalid_grant)"));
        }

        _logger.LogWarning("Google respondió {Status} al renovar el token ({Error})", (int)response.StatusCode, token?.Error);
        return (null, new ProviderReadResult.Unavailable($"Google respondió {(int)response.StatusCode} al renovar el token"));
    }

    internal static async Task<TokenResponse?> ReadTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("error")] string? Error);

    internal sealed record EventsResponse(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("items")] List<GoogleEvent>? Items);

    internal sealed record GoogleEvent(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("location")] string? Location,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("start")] GoogleTime? Start,
        [property: JsonPropertyName("end")] GoogleTime? End,
        [property: JsonPropertyName("attendees")] List<GoogleAttendee>? Attendees);

    internal sealed record GoogleTime(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("dateTime")] DateTimeOffset? DateTime);

    internal sealed record GoogleAttendee(
        [property: JsonPropertyName("self")] bool? Self,
        [property: JsonPropertyName("responseStatus")] string? ResponseStatus);
}

/// <summary>Lo que se guarda cifrado de una conexión de Google (design.md Decisión 2).</summary>
public sealed record GoogleCredentials(string RefreshToken, string? AccessToken, DateTime AccessTokenExpiresAt)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static GoogleCredentials? Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<GoogleCredentials>(json);
            return string.IsNullOrWhiteSpace(parsed?.RefreshToken) ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
