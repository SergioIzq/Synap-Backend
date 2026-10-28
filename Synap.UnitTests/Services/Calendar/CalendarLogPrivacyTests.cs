using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Services.Calendar;
using Synap.Infrastructure.Services.Secrets;
using Synap.UnitTests.Features.Settings;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// Los registros de una lectura fallida llevan usuario, proveedor, estado y causa, y nunca el
/// cuerpo de la respuesta, un título, una descripción ni una credencial
/// (design.md Decisión 10; specs/calendar-integration "Los eventos nunca se almacenan").
/// </summary>
public class CalendarLogPrivacyTests
{
    private const string Title = "Cita con el oncólogo";
    private const string Description = "Llevar los análisis de sangre";
    private const string Password = "contrasena-de-aplicacion";

    /// <summary>Un cuerpo de error que, como hacen algunos servidores, repite lo que no debe salir.</summary>
    private const string LeakyBody = $"{{\"error\":\"server_error\",\"echo\":\"{Title} {Description} {Password}\"}}";

    private readonly CalendarCredentialsProtector _protector =
        new(new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32)));

    private readonly RecordingLogger<CalendarReader> _readerLog = new();
    private readonly RecordingLogger<CalDavCalendarClient> _calDavLog = new();
    private readonly FakeCalendarConnectionRepository _connections = new();

    private CalendarReader Reader(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var http = new HttpClient(new Responder(responder));
        var calDav = new CalDavCalendarClient(http, _calDavLog);
        return new CalendarReader(_connections, new FakeUnitOfWork(), _protector, [calDav], _readerLog);
    }

    private Guid Connect(CalendarCredentials credentials)
    {
        var userId = Guid.NewGuid();
        _connections.Add(CalendarConnection.Create(
            userId, credentials.Provider, "sergio@example.com",
            "https://caldav.example.com/dav/cal/",
            _protector.Protect(credentials), DateTime.UtcNow));
        return userId;
    }

    private IEnumerable<string> AllRecords => _readerLog.Records.Concat(_calDavLog.Records);

    private void AssertNothingLeaked()
    {
        foreach (var secret in new[] { Title, Description, Password, "server_error" })
        {
            Assert.DoesNotContain(AllRecords, r => r.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static HttpResponseMessage Body(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_failed_caldav_read_logs_the_cause_and_nothing_else()
    {
        var userId = Connect(new CalendarCredentials.CalDav("sergio", Password));

        var result = await Reader(_ => Body(HttpStatusCode.BadGateway, LeakyBody))
            .ListEventsAsync(userId, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), 10);

        Assert.IsType<CalendarReadResult.Unavailable>(result);
        Assert.Contains(AllRecords, r => r.Contains(userId.ToString()) && r.Contains("CalDav") && r.Contains("502"));
        AssertNothingLeaked();
    }

    [Fact]
    public async Task An_unreadable_caldav_item_is_counted_not_quoted()
    {
        var userId = Connect(new CalendarCredentials.CalDav("sergio", Password));
        var multiStatus = $"""
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response><d:href>/x.ics</d:href><d:propstat><d:prop>
                <c:calendar-data>BEGIN:VCALENDAR
            BEGIN:VEVENT
            SUMMARY:{Title}
            DESCRIPTION:{Description}
            DTSTART:no-es-fecha
            END:VEVENT
            END:VCALENDAR</c:calendar-data>
              </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            </d:multistatus>
            """;

        await Reader(_ => Body((HttpStatusCode)207, multiStatus))
            .ListEventsAsync(userId, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), 10);

        AssertNothingLeaked();
    }

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
