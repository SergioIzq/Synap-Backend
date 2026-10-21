using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Calendar;
using Synap.Infrastructure.Services.Calendar;
using System.Net;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>calendar-integration tarea 1.4, sobre respuestas con la forma de las reales de Google.</summary>
public class GoogleCalendarClientTests
{
    private static readonly DateTime From = new(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(1);

    private const string EventsJson = """
        {
          "summary": "ana@example.com",
          "items": [
            {"status": "confirmed", "summary": "Viaje a Lisboa", "start": {"date": "2026-11-14"}, "end": {"date": "2026-11-15"}},
            {"status": "confirmed", "summary": "Demo", "location": "Sala 2", "description": "Enseñar el briefing",
             "start": {"dateTime": "2026-11-14T12:00:00+01:00"}, "end": {"dateTime": "2026-11-14T13:00:00+01:00"}},
            {"status": "confirmed", "summary": "Daily", "start": {"dateTime": "2026-11-14T09:30:00+01:00", "timeZone": "Europe/Madrid"},
             "end": {"dateTime": "2026-11-14T09:45:00+01:00"}},
            {"status": "confirmed", "summary": "Comida con proveedores",
             "start": {"dateTime": "2026-11-14T14:00:00+01:00"}, "end": {"dateTime": "2026-11-14T15:00:00+01:00"},
             "attendees": [{"email": "otro@example.com", "responseStatus": "accepted"}, {"self": true, "responseStatus": "declined"}]},
            {"status": "cancelled", "summary": "Reunión anulada",
             "start": {"dateTime": "2026-11-14T16:00:00+01:00"}, "end": {"dateTime": "2026-11-14T17:00:00+01:00"}}
          ]
        }
        """;

    private static string Fresh() => new GoogleCredentials("refresh-1", "access-1", DateTime.UtcNow.AddHours(1)).Serialize();
    private static string Expired() => new GoogleCredentials("refresh-1", "access-viejo", DateTime.UtcNow.AddSeconds(-5)).Serialize();

    private static (GoogleCalendarClient Client, ScriptedHandler Handler) Build(params (string UrlStart, HttpStatusCode Status, string Body)[] script)
    {
        var handler = new ScriptedHandler(script);
        var options = Options.Create(new GoogleCalendarOptions { ClientId = "id", ClientSecret = "secret", RedirectUri = "https://api/cb" });
        return (new GoogleCalendarClient(new HttpClient(handler), options, NullLogger<GoogleCalendarClient>.Instance), handler);
    }

    [Fact]
    public async Task Lee_eventos_con_hora_de_todo_el_dia_y_recurrentes_y_descarta_rechazados_y_cancelados()
    {
        var (client, handler) = Build((GoogleCalendarClient.EventsEndpoint, HttpStatusCode.OK, EventsJson));

        var result = await client.ListEventsAsync("primary", Fresh(), From, To, 20, default);

        var ok = Assert.IsType<ProviderReadResult.Ok>(result);
        Assert.Null(ok.RenewedCredentials);
        Assert.Equal(["Viaje a Lisboa", "Demo", "Daily"], ok.Events.Select(e => e.Title));

        var allDay = ok.Events[0];
        Assert.Equal(new DateOnly(2026, 11, 14), allDay.AllDayDate);

        var demo = ok.Events[1];
        Assert.Equal(new DateTime(2026, 11, 14, 11, 0, 0, DateTimeKind.Utc), demo.StartUtc);
        Assert.Equal("Sala 2", demo.Location);
        Assert.False(demo.IsAllDay);

        // Las recurrencias las expande Google con singleEvents=true: la petición tiene que pedirlo.
        var request = Assert.Single(handler.Requests);
        Assert.Contains("singleEvents=true", request.Url);
        Assert.Contains("orderBy=startTime", request.Url);
        Assert.Contains("/calendars/primary/events", request.Url);
        Assert.Equal("Bearer access-1", request.Authorization);
    }

    [Fact]
    public async Task Un_token_caducado_se_renueva_y_se_devuelve_para_guardarlo()
    {
        var (client, handler) = Build(
            (GoogleCalendarClient.TokenEndpoint, HttpStatusCode.OK, """{"access_token": "access-2", "expires_in": 3599}"""),
            (GoogleCalendarClient.EventsEndpoint, HttpStatusCode.OK, """{"items": []}"""));

        var result = await client.ListEventsAsync("primary", Expired(), From, To, 20, default);

        var ok = Assert.IsType<ProviderReadResult.Ok>(result);
        var renewed = GoogleCredentials.Parse(ok.RenewedCredentials!);
        Assert.Equal("access-2", renewed!.AccessToken);
        Assert.Equal("refresh-1", renewed.RefreshToken);
        Assert.True(renewed.AccessTokenExpiresAt > DateTime.UtcNow.AddMinutes(50));
        Assert.Equal("Bearer access-2", handler.Requests[1].Authorization);
        Assert.Contains("grant_type=refresh_token", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Invalid_grant_es_un_rechazo()
    {
        var (client, _) = Build(
            (GoogleCalendarClient.TokenEndpoint, HttpStatusCode.BadRequest, """{"error": "invalid_grant", "error_description": "Token has been expired or revoked."}"""));

        var result = await client.ListEventsAsync("primary", Expired(), From, To, 20, default);

        Assert.IsType<ProviderReadResult.Rejected>(result);
    }

    [Fact]
    public async Task Un_401_al_leer_es_un_rechazo()
    {
        var (client, _) = Build((GoogleCalendarClient.EventsEndpoint, HttpStatusCode.Unauthorized, """{"error": {"code": 401}}"""));

        Assert.IsType<ProviderReadResult.Rejected>(await client.ListEventsAsync("primary", Fresh(), From, To, 20, default));
    }

    [Fact]
    public async Task Un_500_es_temporal()
    {
        var (client, _) = Build((GoogleCalendarClient.EventsEndpoint, HttpStatusCode.InternalServerError, "oops"));

        Assert.IsType<ProviderReadResult.Unavailable>(await client.ListEventsAsync("primary", Fresh(), From, To, 20, default));
    }

    [Fact]
    public async Task Sin_red_es_temporal()
    {
        var client = new GoogleCalendarClient(
            new HttpClient(new ThrowingHandler()),
            Options.Create(new GoogleCalendarOptions()),
            NullLogger<GoogleCalendarClient>.Instance);

        Assert.IsType<ProviderReadResult.Unavailable>(await client.ListEventsAsync("primary", Fresh(), From, To, 20, default));
    }

    [Fact]
    public async Task Credenciales_ilegibles_son_un_rechazo()
    {
        var (client, handler) = Build();

        Assert.IsType<ProviderReadResult.Rejected>(await client.ListEventsAsync("primary", "{}", From, To, 20, default));
        Assert.Empty(handler.Requests);
    }

    internal sealed record SentRequest(string Url, string? Authorization, string Body);

    /// <summary>Responde según el prefijo de la URL; cada entrada sirve una vez, en orden.</summary>
    internal sealed class ScriptedHandler((string UrlStart, HttpStatusCode Status, string Body)[] script) : HttpMessageHandler
    {
        private readonly List<(string UrlStart, HttpStatusCode Status, string Body)> _script = [.. script];

        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(new SentRequest(
                url,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

            var index = _script.FindIndex(s => url.StartsWith(s.UrlStart, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidOperationException($"Petición no esperada: {request.Method} {url}");
            }

            var entry = _script[index];
            _script.RemoveAt(index);
            return new HttpResponseMessage(entry.Status) { Content = new StringContent(entry.Body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    internal sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no route to host");
    }
}
