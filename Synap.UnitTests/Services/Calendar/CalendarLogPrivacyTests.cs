using Synap.Application.Features.Calendar;
using Synap.Infrastructure.Services.Calendar;
using System.Net;
using System.Text;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// Lo que el cliente CalDAV devuelve y registra al fallar lleva el estado y una categoría, nunca el
/// cuerpo de la respuesta, un título, una descripción ni una credencial (calendar-integration
/// design.md Decisión 10; calendar-store-and-view design.md Decisión 8). La sincronización y los
/// endpoints, que necesitan Postgres, se comprueban en CalendarSyncLogPrivacyTests.
/// </summary>
public class CalendarLogPrivacyTests
{
    private const string Title = "Cita con el oncólogo";
    private const string Description = "Llevar los análisis de sangre";
    private const string Password = "contrasena-de-aplicacion";

    /// <summary>Un cuerpo de error que, como hacen algunos servidores, repite lo que no debe salir.</summary>
    private const string LeakyBody = $"{{\"error\":\"server_error\",\"echo\":\"{Title} {Description} {Password}\"}}";

    private static readonly CalendarCredentials.CalDav Credentials = new("sergio", Password);
    private static readonly Uri Calendar = new("https://caldav.example.com/dav/cal/");

    private readonly RecordingLogger<CalDavCalendarClient> _log = new();

    private CalDavCalendarClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new Responder(responder)), _log);

    private static HttpResponseMessage Body(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private void AssertNothingLeaked(string? cause)
    {
        foreach (var secret in new[] { Title, Description, Password, "server_error" })
        {
            Assert.DoesNotContain(_log.Records, r => r.Contains(secret, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(secret, cause ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task A_failed_sync_report_carries_the_status_and_nothing_else(HttpStatusCode status)
    {
        var result = await Client(_ => Body(status, LeakyBody))
            .SyncCollectionAsync(Calendar, Credentials, "token", CancellationToken.None);

        var failed = Assert.IsType<CalDavResult<SyncCollectionChanges>.Failed>(result);
        Assert.Contains(((int)status).ToString(), failed.Cause);
        AssertNothingLeaked(failed.Cause);
    }

    [Fact]
    public async Task A_failed_multiget_carries_the_status_and_nothing_else()
    {
        var result = await Client(_ => Body(HttpStatusCode.InternalServerError, LeakyBody))
            .MultigetAsync(Calendar, Credentials, [Calendar + "a.ics"], CancellationToken.None);

        var failed = Assert.IsType<CalDavResult<MultigetResult>.Failed>(result);
        AssertNothingLeaked(failed.Cause);
    }

    /// <summary>Un recurso demasiado grande se omite contándolo, sin citar nada de él.</summary>
    [Fact]
    public async Task An_oversized_resource_is_counted_not_quoted()
    {
        var huge = $"SUMMARY:{Title}\n" + new string('x', CalDavCalendarClient.MaxResponseBytes);

        var result = await Client(_ => Body((HttpStatusCode)207, huge))
            .MultigetAsync(Calendar, Credentials, [Calendar + "a.ics"], CancellationToken.None);

        Assert.Empty(Assert.IsType<CalDavResult<MultigetResult>.Ok>(result).Value.Resources);
        Assert.NotEmpty(_log.Records);
        AssertNothingLeaked(null);
    }

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
