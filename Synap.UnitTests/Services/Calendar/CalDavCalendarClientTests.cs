using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Services.Calendar;
using System.Net;
using System.Text;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// calendar-integration tarea 1.5: descubrimiento y lectura CalDAV sobre respuestas grabadas de
/// iCloud, Fastmail y Nextcloud (design.md Decisión 4).
/// </summary>
public class CalDavCalendarClientTests
{
    private static readonly CalendarCredentials.CalDav Credentials = new("sergio@example.com", "abcd-efgh-ijkl");

    private readonly List<(HttpMethod Method, Uri Url, string Depth, string Body)> _requests = [];

    private CalDavCalendarClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new Handler(responder, _requests)), new RecordingLogger<CalDavCalendarClient>());

    /// <summary>Responde según método y ruta; lo demás, 404.</summary>
    private CalDavCalendarClient Server(params (string Method, string Url, HttpStatusCode Status, string Body)[] routes) =>
        Client(request =>
        {
            var route = routes.FirstOrDefault(r =>
                r.Method == request.Method.Method && r.Url == request.RequestUri!.AbsoluteUri);
            return route.Url is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Xml(route.Status, route.Body);
        });

    private static HttpResponseMessage Xml(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private static Task<CalendarProviderRead> ReadAsync(
        CalDavCalendarClient client, string calendarUrl, DateTime fromUtc, DateTime toUtc) =>
        client.ReadAsync(new CalendarProviderRequest(Guid.NewGuid(), calendarUrl, Credentials, fromUtc, toUtc, 50), CancellationToken.None);

    private static IReadOnlyList<CalendarEvent> Events(CalendarProviderRead read) =>
        Assert.IsType<CalendarReadResult.Ok>(read.Result).Events;

    // ---- Descubrimiento ----

    [Fact]
    public async Task ICloud_is_found_through_well_known_principal_and_home()
    {
        var client = Server(
            ("PROPFIND", "https://caldav.icloud.com/", (HttpStatusCode)207, CalDavRecordings.ICloudRootNotCalendar),
            ("PROPFIND", "https://caldav.icloud.com/.well-known/caldav", (HttpStatusCode)207, CalDavRecordings.ICloudWellKnownPrincipal),
            ("PROPFIND", "https://caldav.icloud.com/123456789/principal/", (HttpStatusCode)207, CalDavRecordings.ICloudPrincipal),
            ("PROPFIND", "https://p42-caldav.icloud.com/123456789/calendars/", (HttpStatusCode)207, CalDavRecordings.ICloudHome));

        var result = await client.DiscoverAsync(new Uri("https://caldav.icloud.com/"), "sergio@icloud.com", "app-pass");

        Assert.Equal(
            new CalDavDiscoveryResult.Found("https://p42-caldav.icloud.com/123456789/calendars/home/"),
            result);
        Assert.Equal("1", _requests.Last().Depth);
    }

    [Fact]
    public async Task Fastmail_uses_its_default_calendar()
    {
        var client = Server(
            ("PROPFIND", "https://caldav.fastmail.com/dav/", (HttpStatusCode)207, CalDavRecordings.FastmailRoot),
            ("PROPFIND", "https://caldav.fastmail.com/dav/principals/user/sergio@fastmail.com/", (HttpStatusCode)207, CalDavRecordings.FastmailPrincipal));

        var result = await client.DiscoverAsync(new Uri("https://caldav.fastmail.com/dav/"), "sergio@fastmail.com", "app-pass");

        Assert.Equal(
            new CalDavDiscoveryResult.Found("https://caldav.fastmail.com/dav/calendars/user/sergio@fastmail.com/Default/"),
            result);
    }

    [Fact]
    public async Task A_nextcloud_calendar_url_is_the_calendar_itself()
    {
        var client = Server(
            ("PROPFIND", CalDavRecordings.NextcloudCalendarUrl, (HttpStatusCode)207, CalDavRecordings.NextcloudCalendarItself));

        var result = await client.DiscoverAsync(new Uri(CalDavRecordings.NextcloudCalendarUrl), "sergio", "app-pass");

        Assert.Equal(new CalDavDiscoveryResult.Found(CalDavRecordings.NextcloudCalendarUrl), result);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task Rejected_credentials_are_told_apart()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await client.DiscoverAsync(new Uri("https://caldav.fastmail.com/dav/"), "sergio", "mala");

        Assert.IsType<CalDavDiscoveryResult.CredentialsRejected>(result);
    }

    [Fact]
    public async Task An_unreachable_server_is_told_apart()
    {
        var client = Client(_ => throw new HttpRequestException("sin red", null, HttpStatusCode.ServiceUnavailable));

        var result = await client.DiscoverAsync(new Uri("https://caldav.example.com/"), "sergio", "app-pass");

        Assert.IsType<CalDavDiscoveryResult.Unreachable>(result);
    }

    [Fact]
    public async Task An_account_with_only_task_lists_has_no_calendar()
    {
        var client = Server(
            ("PROPFIND", "https://caldav.fastmail.com/dav/", (HttpStatusCode)207, CalDavRecordings.FastmailRoot),
            ("PROPFIND", "https://caldav.fastmail.com/dav/principals/user/sergio@fastmail.com/", (HttpStatusCode)207, """
                <?xml version="1.0"?>
                <d:multistatus xmlns:d="DAV:" xmlns:cal="urn:ietf:params:xml:ns:caldav">
                  <d:response><d:href>/dav/principals/user/sergio@fastmail.com/</d:href>
                    <d:propstat><d:prop><cal:calendar-home-set><d:href>/dav/calendars/user/sergio/</d:href></cal:calendar-home-set></d:prop>
                    <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                </d:multistatus>
                """),
            ("PROPFIND", "https://caldav.fastmail.com/dav/calendars/user/sergio/", (HttpStatusCode)207, CalDavRecordings.HomeWithoutEvents));

        var result = await client.DiscoverAsync(new Uri("https://caldav.fastmail.com/dav/"), "sergio", "app-pass");

        Assert.IsType<CalDavDiscoveryResult.NoCalendar>(result);
    }

    // ---- Lectura ----

    [Fact]
    public async Task The_report_asks_the_server_to_expand_the_range()
    {
        var client = Server(("REPORT", CalDavRecordings.NextcloudCalendarUrl, (HttpStatusCode)207, CalDavRecordings.NextcloudReportNotExpanded));

        await ReadAsync(client, CalDavRecordings.NextcloudCalendarUrl,
            new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc));

        var report = Assert.Single(_requests);
        Assert.Equal("REPORT", report.Method.Method);
        Assert.Equal("1", report.Depth);
        Assert.Contains("expand", report.Body);
        Assert.Contains("start=\"20261012T000000Z\"", report.Body);
        Assert.Contains("end=\"20261026T000000Z\"", report.Body);
    }

    /// <summary>`TZID` con VTIMEZONE embebido: 18:00 en Ciudad de México son las 00:00 UTC del día siguiente.</summary>
    [Fact]
    public async Task ICloud_times_with_tzid_come_back_in_utc_with_their_details()
    {
        var url = "https://p42-caldav.icloud.com/123456789/calendars/home/";
        var client = Server(("REPORT", url, (HttpStatusCode)207, CalDavRecordings.ICloudReport));

        var events = Events(await ReadAsync(client, url,
            new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc)));

        var dinner = Assert.Single(events);
        Assert.Equal("Cena con Marta", dinner.Title);
        Assert.Equal(new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc), dinner.StartUtc);
        Assert.Equal(new DateTime(2026, 11, 14, 1, 0, 0, DateTimeKind.Utc), dinner.EndUtc);
        Assert.False(dinner.IsAllDay);
        Assert.Equal("Roma Norte", dinner.Location);
        Assert.Equal("Llevar el libro, y el regalo", dinner.Description);
    }

    /// <summary>`VALUE=DATE` y `STATUS:CANCELLED`.</summary>
    [Fact]
    public async Task Fastmail_all_day_events_keep_their_days_and_cancelled_ones_are_left_out()
    {
        var url = "https://caldav.fastmail.com/dav/calendars/user/sergio@fastmail.com/Default/";
        var client = Server(("REPORT", url, (HttpStatusCode)207, CalDavRecordings.FastmailReport));

        var events = Events(await ReadAsync(client, url,
            new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc)));

        var trip = Assert.Single(events);
        Assert.Equal("Escapada a Toledo", trip.Title);
        Assert.Equal(new DateOnly(2026, 11, 14), trip.AllDayDate);
        Assert.Equal(new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), trip.EndUtc);
    }

    /// <summary>
    /// Nextcloud sin expandir: la regla se expande en local, con la ocurrencia movida y el cambio
    /// de hora del 25 de octubre; la invitación rechazada (`DECLINED`) no se devuelve.
    /// </summary>
    [Fact]
    public async Task Nextcloud_rules_are_expanded_locally_and_declined_invitations_left_out()
    {
        var client = Server(("REPORT", CalDavRecordings.NextcloudCalendarUrl, (HttpStatusCode)207, CalDavRecordings.NextcloudReportNotExpanded));

        var events = Events(await ReadAsync(client, CalDavRecordings.NextcloudCalendarUrl,
            new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.All(events, e => Assert.Equal("Reunión de sprint", e.Title));
        Assert.Equal(
            [
                new DateTime(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc), // 09:00 CEST
                new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc), // movida a las 10:00 CEST
                new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc), // 09:00 CET, tras el cambio de hora
            ],
            events.Select(e => e.StartUtc));
    }

    [Fact]
    public async Task Rejected_credentials_on_reading_need_reconnecting()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var read = await ReadAsync(client, CalDavRecordings.NextcloudCalendarUrl, DateTime.UtcNow, DateTime.UtcNow.AddDays(1));

        Assert.IsType<CalendarReadResult.NeedsReconnect>(read.Result);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_failing_server_is_unavailable(HttpStatusCode status)
    {
        var client = Client(_ => new HttpResponseMessage(status));

        var read = await ReadAsync(client, CalDavRecordings.NextcloudCalendarUrl, DateTime.UtcNow, DateTime.UtcNow.AddDays(1));

        Assert.IsType<CalendarReadResult.Unavailable>(read.Result);
    }

    [Fact]
    public async Task An_unparseable_item_does_not_cost_the_others()
    {
        var url = "https://caldav.fastmail.com/dav/calendars/user/sergio@fastmail.com/Default/";
        var broken = CalDavRecordings.FastmailReport.Replace("DTSTART:20261114T100000Z", "DTSTART:esto-no-es-una-fecha");
        var client = Server(("REPORT", url, (HttpStatusCode)207, broken));

        var events = Events(await ReadAsync(client, url,
            new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Contains(events, e => e.Title == "Escapada a Toledo");
    }

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> responder, List<(HttpMethod, Uri, string, string)> requests)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var depth = request.Headers.TryGetValues("Depth", out var values) ? values.Single() : string.Empty;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add((request.Method, request.RequestUri!, depth, body));
            return responder(request);
        }
    }
}
