using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Services.Calendar;
using System.Net;
using System.Text;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// El cliente CalDAV sobre respuestas grabadas: el descubrimiento del home en iCloud, Fastmail y
/// Nextcloud (calendar-integration tarea 1.5), y las operaciones de la sincronización en Radicale y
/// Nextcloud (calendar-store-and-view tarea 1.3, design.md Decisión 3).
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

    private static T Ok<T>(CalDavResult<T> result) => Assert.IsType<CalDavResult<T>.Ok>(result).Value;

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

        Assert.Equal(new CalDavDiscoveryResult.Found("https://p42-caldav.icloud.com/123456789/calendars/"), result);
        Assert.Equal("1", _requests.Last().Depth);
    }

    /// <summary>Fastmail expone su calendario por defecto, pero se conecta el home con todos.</summary>
    [Fact]
    public async Task Fastmail_is_found_by_its_home_not_its_default_calendar()
    {
        var client = Server(
            ("PROPFIND", "https://caldav.fastmail.com/dav/", (HttpStatusCode)207, CalDavRecordings.FastmailRoot),
            ("PROPFIND", "https://caldav.fastmail.com/dav/principals/user/sergio@fastmail.com/", (HttpStatusCode)207, CalDavRecordings.FastmailPrincipal),
            ("PROPFIND", "https://caldav.fastmail.com/dav/calendars/user/sergio@fastmail.com/", (HttpStatusCode)207, """
                <?xml version="1.0"?>
                <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
                  <D:response><D:href>/dav/calendars/user/sergio@fastmail.com/Default/</D:href>
                    <D:propstat><D:prop><D:resourcetype><D:collection/><C:calendar/></D:resourcetype></D:prop>
                    <D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
                </D:multistatus>
                """));

        var result = await client.DiscoverAsync(new Uri("https://caldav.fastmail.com/dav/"), "sergio@fastmail.com", "app-pass");

        Assert.Equal(new CalDavDiscoveryResult.Found("https://caldav.fastmail.com/dav/calendars/user/sergio@fastmail.com/"), result);
    }

    /// <summary>Desde la URL de un calendario se sube por su principal hasta el home.</summary>
    [Fact]
    public async Task A_nextcloud_calendar_url_leads_to_its_home()
    {
        var client = Server(
            ("PROPFIND", CalDavRecordings.NextcloudCalendarUrl, (HttpStatusCode)207, CalDavRecordings.NextcloudCalendarItself),
            ("PROPFIND", "https://nube.example.com/remote.php/dav/principals/users/sergio/", (HttpStatusCode)207, CalDavSyncRecordings.NextcloudPrincipal),
            ("PROPFIND", CalDavSyncRecordings.NextcloudHome, (HttpStatusCode)207, CalDavSyncRecordings.NextcloudHomeListing));

        var result = await client.DiscoverAsync(new Uri(CalDavRecordings.NextcloudCalendarUrl), "sergio", "app-pass");

        Assert.Equal(new CalDavDiscoveryResult.Found(CalDavSyncRecordings.NextcloudHome), result);
    }

    /// <summary>Si el servidor no deja llegar al home, el calendario hace de home de uno solo.</summary>
    [Fact]
    public async Task A_calendar_without_a_reachable_home_is_its_own_home()
    {
        var client = Server(
            ("PROPFIND", CalDavRecordings.NextcloudCalendarUrl, (HttpStatusCode)207, CalDavRecordings.NextcloudCalendarItself));

        var result = await client.DiscoverAsync(new Uri(CalDavRecordings.NextcloudCalendarUrl), "sergio", "app-pass");

        Assert.Equal(new CalDavDiscoveryResult.Found(CalDavRecordings.NextcloudCalendarUrl), result);
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

    // ---- Colecciones del home ----

    [Fact]
    public async Task Radicale_lists_only_event_calendars_with_name_color_and_ctag()
    {
        var client = Server(("PROPFIND", CalDavSyncRecordings.RadicaleHome, (HttpStatusCode)207, CalDavSyncRecordings.RadicaleHomeListing));

        var calendars = Ok(await client.ListCalendarsAsync(new Uri(CalDavSyncRecordings.RadicaleHome), Credentials, CancellationToken.None));

        Assert.Equal([CalDavSyncRecordings.RadicalePersonal, CalDavSyncRecordings.RadicaleWork], calendars.Select(c => c.Href));
        var personal = calendars[0];
        Assert.Equal("Personal", personal.DisplayName);
        Assert.Equal("#ff0000ff", personal.Color);
        Assert.Equal("\"6c1b8e6e5fd2a0e1\"", personal.CTag);
        Assert.True(personal.SupportsSyncCollection);
        Assert.False(personal.IsRequested);
        Assert.Null(calendars[1].Color);

        var propfind = Assert.Single(_requests);
        Assert.Equal("1", propfind.Depth);
        Assert.Contains("calendar-color", propfind.Body);
        Assert.Contains("getctag", propfind.Body);
        Assert.Contains("supported-report-set", propfind.Body);
    }

    [Fact]
    public async Task Nextcloud_inbox_and_trash_are_not_calendars()
    {
        var client = Server(("PROPFIND", CalDavSyncRecordings.NextcloudHome, (HttpStatusCode)207, CalDavSyncRecordings.NextcloudHomeListing));

        var calendars = Ok(await client.ListCalendarsAsync(new Uri(CalDavSyncRecordings.NextcloudHome), Credentials, CancellationToken.None));

        var personal = Assert.Single(calendars);
        Assert.Equal(CalDavSyncRecordings.NextcloudPersonal, personal.Href);
        Assert.Equal("#0082C9", personal.Color);
        Assert.True(personal.SupportsSyncCollection);
    }

    /// <summary>Una conexión antigua guardaba un calendario: listarlo lo devuelve marcado como el pedido.</summary>
    [Fact]
    public async Task Listing_a_calendar_instead_of_a_home_says_so()
    {
        var client = Server(("PROPFIND", CalDavRecordings.NextcloudCalendarUrl, (HttpStatusCode)207, CalDavRecordings.NextcloudCalendarItself));

        var calendars = Ok(await client.ListCalendarsAsync(new Uri(CalDavRecordings.NextcloudCalendarUrl), Credentials, CancellationToken.None));

        Assert.True(Assert.Single(calendars).IsRequested);
    }

    // ---- sync-collection ----

    [Fact]
    public async Task Radicale_initial_sync_lists_everything_with_the_new_token()
    {
        var client = Server(("REPORT", CalDavSyncRecordings.RadicalePersonal, (HttpStatusCode)207, CalDavSyncRecordings.RadicaleInitialSync));

        var changes = Ok(await client.SyncCollectionAsync(new Uri(CalDavSyncRecordings.RadicalePersonal), Credentials, null, CancellationToken.None));

        Assert.Equal(
            [
                new RemoteETag("https://radicale.example.com/sergio/personal/cena.ics", "\"e-cena-1\""),
                new RemoteETag("https://radicale.example.com/sergio/personal/gym%20lunes.ics", "\"e-gym-1\""),
            ],
            changes.Changed);
        Assert.Empty(changes.Removed);
        Assert.Equal("http://radicale.org/ns/sync/9d1e5b3a", changes.NewToken);
        Assert.False(changes.Truncated);

        var report = Assert.Single(_requests);
        Assert.Equal("0", report.Depth);
        Assert.Contains("sync-collection", report.Body);
        Assert.Contains("<d:sync-token></d:sync-token>", report.Body);
    }

    [Fact]
    public async Task Radicale_incremental_sync_tells_changed_from_removed()
    {
        var client = Server(("REPORT", CalDavSyncRecordings.RadicalePersonal, (HttpStatusCode)207, CalDavSyncRecordings.RadicaleIncrementalSync));

        var changes = Ok(await client.SyncCollectionAsync(
            new Uri(CalDavSyncRecordings.RadicalePersonal), Credentials, "http://radicale.org/ns/sync/9d1e5b3a", CancellationToken.None));

        Assert.Equal([new RemoteETag("https://radicale.example.com/sergio/personal/cena.ics", "\"e-cena-2\"")], changes.Changed);
        Assert.Equal(["https://radicale.example.com/sergio/personal/gym%20lunes.ics"], changes.Removed);
        Assert.Equal("http://radicale.org/ns/sync/0f77c2d4", changes.NewToken);
        Assert.Contains("http://radicale.org/ns/sync/9d1e5b3a", _requests.Single().Body);
    }

    [Fact]
    public async Task Nextcloud_incremental_sync_tells_changed_from_removed()
    {
        var client = Server(("REPORT", CalDavSyncRecordings.NextcloudPersonal, (HttpStatusCode)207, CalDavSyncRecordings.NextcloudIncrementalSync));

        var changes = Ok(await client.SyncCollectionAsync(
            new Uri(CalDavSyncRecordings.NextcloudPersonal), Credentials, "http://sabre.io/ns/sync/42", CancellationToken.None));

        Assert.Equal([new RemoteETag(CalDavSyncRecordings.NextcloudPersonal + "sprint.ics", "\"8a1f\"")], changes.Changed);
        Assert.Equal([CalDavSyncRecordings.NextcloudPersonal + "viejo.ics"], changes.Removed);
        Assert.Equal("http://sabre.io/ns/sync/43", changes.NewToken);
    }

    [Theory]
    [InlineData(CalDavSyncRecordings.RadicalePersonal, CalDavSyncRecordings.RadicaleInvalidToken, HttpStatusCode.Forbidden)]
    [InlineData(CalDavSyncRecordings.NextcloudPersonal, CalDavSyncRecordings.NextcloudInvalidToken, HttpStatusCode.Forbidden)]
    [InlineData(CalDavSyncRecordings.NextcloudPersonal, CalDavSyncRecordings.NextcloudInvalidToken, HttpStatusCode.Conflict)]
    public async Task A_rejected_token_is_told_apart_from_rejected_credentials(string url, string body, HttpStatusCode status)
    {
        var client = Server(("REPORT", url, status, body));

        var result = await client.SyncCollectionAsync(new Uri(url), Credentials, "caducado", CancellationToken.None);

        Assert.IsType<CalDavResult<SyncCollectionChanges>.TokenInvalid>(result);
    }

    [Fact]
    public async Task A_forbidden_without_the_precondition_is_rejected_credentials()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await client.SyncCollectionAsync(new Uri(CalDavSyncRecordings.RadicalePersonal), Credentials, "t", CancellationToken.None);

        Assert.IsType<CalDavResult<SyncCollectionChanges>.Rejected>(result);
    }

    [Fact]
    public async Task A_truncated_sync_says_so()
    {
        var client = Server(("REPORT", CalDavSyncRecordings.NextcloudPersonal, (HttpStatusCode)207, CalDavSyncRecordings.NextcloudTruncatedSync));

        var changes = Ok(await client.SyncCollectionAsync(new Uri(CalDavSyncRecordings.NextcloudPersonal), Credentials, null, CancellationToken.None));

        Assert.True(changes.Truncated);
        Assert.Single(changes.Changed);
        Assert.Equal("http://sabre.io/ns/sync/44", changes.NewToken);
    }

    // ---- calendar-multiget ----

    /// <summary>El `404` de un recurso que se borró entre el cambio y la descarga vuelve como ausente.</summary>
    [Fact]
    public async Task Multiget_returns_the_ics_and_the_missing()
    {
        var client = Server(("REPORT", CalDavSyncRecordings.RadicalePersonal, (HttpStatusCode)207, CalDavSyncRecordings.RadicaleMultiget));
        var hrefs = new[]
        {
            "https://radicale.example.com/sergio/personal/cena.ics",
            "https://radicale.example.com/sergio/personal/borrado.ics",
        };

        var result = Ok(await client.MultigetAsync(new Uri(CalDavSyncRecordings.RadicalePersonal), Credentials, hrefs, CancellationToken.None));

        var cena = Assert.Single(result.Resources);
        Assert.Equal(hrefs[0], cena.Href);
        Assert.Equal("\"e-cena-2\"", cena.ETag);
        Assert.Contains("SUMMARY:Cena con Marta", cena.Ics);
        Assert.Equal([hrefs[1]], result.Missing);

        var body = _requests.Single().Body;
        Assert.Contains("calendar-multiget", body);
        Assert.Contains("<d:href>/sergio/personal/cena.ics</d:href>", body);
        Assert.Contains("<d:href>/sergio/personal/borrado.ics</d:href>", body);
    }

    [Fact]
    public async Task Multiget_goes_in_batches()
    {
        var client = Client(_ => Xml((HttpStatusCode)207, """<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" />"""));
        var hrefs = Enumerable.Range(0, CalDavCalendarClient.MultigetBatchSize * 2 + 1)
            .Select(i => $"{CalDavSyncRecordings.RadicalePersonal}{i}.ics")
            .ToList();

        Ok(await client.MultigetAsync(new Uri(CalDavSyncRecordings.RadicalePersonal), Credentials, hrefs, CancellationToken.None));

        Assert.Equal(3, _requests.Count);
        Assert.All(_requests, r => Assert.Equal("REPORT", r.Method.Method));
    }

    // ---- ETags ----

    [Fact]
    public async Task Nextcloud_etags_leave_out_the_calendar_itself()
    {
        var client = Server(("PROPFIND", CalDavSyncRecordings.NextcloudPersonal, (HttpStatusCode)207, CalDavSyncRecordings.NextcloudETags));

        var etags = Ok(await client.ListETagsAsync(new Uri(CalDavSyncRecordings.NextcloudPersonal), Credentials, CancellationToken.None));

        Assert.Equal(
            [
                new RemoteETag(CalDavSyncRecordings.NextcloudPersonal + "sprint.ics", "\"8a1f\""),
                new RemoteETag(CalDavSyncRecordings.NextcloudPersonal + "cumple.ics", "\"77c0\""),
            ],
            etags);
        Assert.Equal("1", _requests.Single().Depth);
    }

    // ---- Fallos ----

    [Fact]
    public async Task Rejected_credentials_on_syncing_need_reconnecting()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await client.ListCalendarsAsync(new Uri(CalDavSyncRecordings.RadicaleHome), Credentials, CancellationToken.None);

        Assert.IsType<CalDavResult<IReadOnlyList<RemoteCalendar>>.Rejected>(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_failing_server_is_a_server_error(HttpStatusCode status)
    {
        var client = Client(_ => new HttpResponseMessage(status));

        var result = await client.ListETagsAsync(new Uri(CalDavSyncRecordings.NextcloudPersonal), Credentials, CancellationToken.None);

        var failed = Assert.IsType<CalDavResult<IReadOnlyList<RemoteETag>>.Failed>(result);
        Assert.Equal(CalendarSyncErrors.ServerError, failed.Category);
    }

    [Fact]
    public async Task An_unreachable_server_is_unreachable()
    {
        var client = Client(_ => throw new HttpRequestException("sin red", null, HttpStatusCode.ServiceUnavailable));

        var result = await client.SyncCollectionAsync(new Uri(CalDavSyncRecordings.RadicalePersonal), Credentials, null, CancellationToken.None);

        var failed = Assert.IsType<CalDavResult<SyncCollectionChanges>.Failed>(result);
        Assert.Equal(CalendarSyncErrors.Unreachable, failed.Category);
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
