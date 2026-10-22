using Microsoft.Extensions.Options;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Services.Calendar;
using System.Net;
using System.Text;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// calendar-integration tarea 1.4: el adaptador de Google sobre respuestas grabadas de `events.list`
/// y del endpoint de tokens (design.md Decisión 3).
/// </summary>
public class GoogleCalendarClientTests
{
    private static readonly DateTime FromUtc = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ToUtc = new(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Una respuesta de `events.list` grabada (recortada a los campos que se usan): un evento con
    /// hora, uno de todo el día, una ocurrencia de un evento recurrente, una invitación rechazada y
    /// un evento cancelado.
    /// </summary>
    private const string RecordedEvents = """
        {
          "kind": "calendar#events",
          "summary": "sergio@gmail.com",
          "timeZone": "Europe/Madrid",
          "items": [
            {
              "kind": "calendar#event", "id": "a1", "status": "confirmed",
              "summary": "Demo con el cliente", "location": "Sala 2",
              "description": "Preparar la demo del módulo de pagos",
              "start": { "dateTime": "2026-10-07T16:00:00+02:00", "timeZone": "Europe/Madrid" },
              "end": { "dateTime": "2026-10-07T17:00:00+02:00", "timeZone": "Europe/Madrid" }
            },
            {
              "kind": "calendar#event", "id": "a2", "status": "confirmed",
              "summary": "Puente del Pilar",
              "start": { "date": "2026-10-12" },
              "end": { "date": "2026-10-13" }
            },
            {
              "kind": "calendar#event", "id": "r1_20261012T070000Z", "status": "confirmed",
              "summary": "Reunión de sprint", "recurringEventId": "r1",
              "originalStartTime": { "dateTime": "2026-10-12T09:00:00+02:00" },
              "start": { "dateTime": "2026-10-12T09:00:00+02:00", "timeZone": "Europe/Madrid" },
              "end": { "dateTime": "2026-10-12T09:30:00+02:00", "timeZone": "Europe/Madrid" }
            },
            {
              "kind": "calendar#event", "id": "d1", "status": "confirmed",
              "summary": "Comida de equipo",
              "start": { "dateTime": "2026-10-08T14:00:00+02:00" },
              "end": { "dateTime": "2026-10-08T15:00:00+02:00" },
              "attendees": [
                { "email": "ana@example.com", "organizer": true, "responseStatus": "accepted" },
                { "email": "sergio@gmail.com", "self": true, "responseStatus": "declined" }
              ]
            },
            {
              "kind": "calendar#event", "id": "c1", "status": "cancelled",
              "summary": "Retro cancelada",
              "start": { "dateTime": "2026-10-09T10:00:00+02:00" },
              "end": { "dateTime": "2026-10-09T11:00:00+02:00" }
            }
          ]
        }
        """;

    private const string RecordedToken = """
        { "access_token": "ya29.nuevo", "expires_in": 3599, "scope": "https://www.googleapis.com/auth/calendar.events.readonly", "token_type": "Bearer" }
        """;

    private const string RecordedInvalidGrant = """
        { "error": "invalid_grant", "error_description": "Token has been expired or revoked." }
        """;

    private static readonly CalendarCredentials.Google Fresh =
        new("1//refresh", "ya29.vigente", DateTime.UtcNow.AddMinutes(30));

    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string> _bodies = [];

    private GoogleCalendarClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder, bool available = true)
    {
        var handler = new RecordingHandler(responder, _requests, _bodies);
        var options = Options.Create(available
            ? new GoogleCalendarOptions { ClientId = "id", ClientSecret = "secreto", RedirectUri = "https://synap.test/cb" }
            : new GoogleCalendarOptions());

        return new GoogleCalendarClient(new HttpClient(handler), options, new RecordingLogger<GoogleCalendarClient>());
    }

    private static Task<CalendarProviderRead> ReadAsync(GoogleCalendarClient client, CalendarCredentials.Google credentials)
        => client.ReadAsync(
            new CalendarProviderRequest(Guid.NewGuid(), "primary", credentials, FromUtc, ToUtc, 50),
            CancellationToken.None);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static bool IsToken(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri == GoogleCalendarClient.TokenEndpoint;

    [Fact]
    public async Task It_asks_for_the_expanded_occurrences_of_the_primary_calendar()
    {
        await ReadAsync(Client(_ => Json(HttpStatusCode.OK, RecordedEvents)), Fresh);

        var request = Assert.Single(_requests);
        var uri = request.RequestUri!.AbsoluteUri;
        Assert.StartsWith("https://www.googleapis.com/calendar/v3/calendars/primary/events?", uri);
        Assert.Contains("singleEvents=true", uri);
        Assert.Contains("orderBy=startTime", uri);
        Assert.Contains("timeMin=2026-10-07T00%3A00%3A00Z", uri);
        Assert.Contains("timeMax=2026-10-14T00%3A00%3A00Z", uri);
        Assert.Equal("Bearer ya29.vigente", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task A_timed_event_comes_back_in_utc_with_its_details()
    {
        var read = await ReadAsync(Client(_ => Json(HttpStatusCode.OK, RecordedEvents)), Fresh);

        var demo = Assert.IsType<CalendarReadResult.Ok>(read.Result).Events.Single(e => e.Title == "Demo con el cliente");
        Assert.Equal(new DateTime(2026, 10, 7, 14, 0, 0, DateTimeKind.Utc), demo.StartUtc);
        Assert.Equal(new DateTime(2026, 10, 7, 15, 0, 0, DateTimeKind.Utc), demo.EndUtc);
        Assert.False(demo.IsAllDay);
        Assert.Equal("Sala 2", demo.Location);
        Assert.Equal("Preparar la demo del módulo de pagos", demo.Description);
        Assert.Null(read.RenewedCredentials);
    }

    /// <summary>specs/calendar-integration "Evento de todo el día": no se mueve con la zona horaria.</summary>
    [Fact]
    public async Task An_all_day_event_keeps_its_written_day()
    {
        var read = await ReadAsync(Client(_ => Json(HttpStatusCode.OK, RecordedEvents)), Fresh);

        var bridge = Assert.IsType<CalendarReadResult.Ok>(read.Result).Events.Single(e => e.Title == "Puente del Pilar");
        Assert.Equal(new DateOnly(2026, 10, 12), bridge.AllDayDate);
    }

    [Fact]
    public async Task A_recurring_event_comes_back_as_its_occurrence()
    {
        var read = await ReadAsync(Client(_ => Json(HttpStatusCode.OK, RecordedEvents)), Fresh);

        var sprint = Assert.IsType<CalendarReadResult.Ok>(read.Result).Events.Single(e => e.Title == "Reunión de sprint");
        Assert.Equal(new DateTime(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc), sprint.StartUtc);
    }

    /// <summary>specs/calendar-integration "Evento rechazado o cancelado".</summary>
    [Fact]
    public async Task Declined_and_cancelled_events_are_left_out()
    {
        var read = await ReadAsync(Client(_ => Json(HttpStatusCode.OK, RecordedEvents)), Fresh);

        var titles = Assert.IsType<CalendarReadResult.Ok>(read.Result).Events.Select(e => e.Title).ToList();
        Assert.Equal(3, titles.Count);
        Assert.DoesNotContain("Comida de equipo", titles);
        Assert.DoesNotContain("Retro cancelada", titles);
    }

    /// <summary>design.md Decisión 3: renovación bajo demanda, y las credenciales nuevas vuelven para guardarse.</summary>
    [Fact]
    public async Task An_expiring_access_token_is_renewed_before_reading()
    {
        var expiring = new CalendarCredentials.Google("1//refresh", "ya29.viejo", DateTime.UtcNow.AddSeconds(30));
        var client = Client(r => IsToken(r) ? Json(HttpStatusCode.OK, RecordedToken) : Json(HttpStatusCode.OK, RecordedEvents));

        var read = await ReadAsync(client, expiring);

        Assert.Equal(2, _requests.Count);
        Assert.True(IsToken(_requests[0]));
        Assert.Contains("grant_type=refresh_token", _bodies[0]);
        Assert.Contains("refresh_token=1%2F%2Frefresh", _bodies[0]);
        Assert.Equal("Bearer ya29.nuevo", _requests[1].Headers.Authorization!.ToString());
        Assert.IsType<CalendarReadResult.Ok>(read.Result);
        var renewed = Assert.IsType<CalendarCredentials.Google>(read.RenewedCredentials);
        Assert.Equal("ya29.nuevo", renewed.AccessToken);
        Assert.Equal("1//refresh", renewed.RefreshToken);
        Assert.InRange(renewed.AccessTokenExpiresAt!.Value, DateTime.UtcNow.AddMinutes(58), DateTime.UtcNow.AddMinutes(61));
    }

    [Fact]
    public async Task A_rejected_access_token_is_renewed_once_and_retried()
    {
        var calls = 0;
        var client = Client(r => IsToken(r)
            ? Json(HttpStatusCode.OK, RecordedToken)
            : ++calls == 1 ? Json(HttpStatusCode.Unauthorized, "{}") : Json(HttpStatusCode.OK, RecordedEvents));

        var read = await ReadAsync(client, Fresh);

        Assert.IsType<CalendarReadResult.Ok>(read.Result);
        Assert.Equal("ya29.nuevo", Assert.IsType<CalendarCredentials.Google>(read.RenewedCredentials).AccessToken);
    }

    /// <summary>specs/calendar-integration "Acceso revocado".</summary>
    [Fact]
    public async Task Invalid_grant_needs_reconnecting()
    {
        var expired = new CalendarCredentials.Google("1//refresh");
        var client = Client(r => IsToken(r) ? Json(HttpStatusCode.BadRequest, RecordedInvalidGrant) : Json(HttpStatusCode.OK, RecordedEvents));

        var read = await ReadAsync(client, expired);

        Assert.IsType<CalendarReadResult.NeedsReconnect>(read.Result);
        Assert.Single(_requests);
    }

    /// <summary>specs/calendar-integration "Proveedor caído": temporal, no se marca.</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_failing_google_is_unavailable(HttpStatusCode status)
    {
        var read = await ReadAsync(Client(_ => Json(status, "{}")), Fresh);

        Assert.IsType<CalendarReadResult.Unavailable>(read.Result);
    }

    [Fact]
    public async Task An_unreachable_google_is_unavailable()
    {
        var read = await ReadAsync(Client(_ => throw new HttpRequestException("sin red")), Fresh);

        Assert.IsType<CalendarReadResult.Unavailable>(read.Result);
    }

    [Fact]
    public async Task Without_an_oauth_client_nothing_is_asked()
    {
        var read = await ReadAsync(Client(_ => Json(HttpStatusCode.OK, RecordedEvents), available: false), Fresh);

        Assert.IsType<CalendarReadResult.Unavailable>(read.Result);
        Assert.Empty(_requests);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder, List<HttpRequestMessage> requests, List<string> bodies)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return responder(request);
        }
    }
}
