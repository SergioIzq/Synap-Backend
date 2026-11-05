using Microsoft.Extensions.Logging;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// El cliente CalDAV: descubre el home al conectar y hace las lecturas de la sincronización
/// (calendar-store-and-view design.md Decisión 3). Las peticiones son nuestras porque son pocas y
/// fijas (PROPFIND y tres REPORT); el iCalendar se guarda tal cual y lo lee Ical.Net después.
///
/// La defensa contra SSRF está en el handler del <see cref="HttpClient"/> (calendar-integration
/// design.md Decisión 5). Aquí se exige HTTPS, no se siguen redirecciones a otro host y se limita
/// el tamaño de cada respuesta. Nunca se registra ni se devuelve el cuerpo de una respuesta.
/// </summary>
public sealed class CalDavCalendarClient : ICalDavSyncClient, ICalDavDiscovery
{
    /// <summary>Tamaño máximo de una respuesta; un lote de multiget cabe de sobra.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>Recursos por `calendar-multiget`. Un lote demasiado grande se parte en dos.</summary>
    public const int MultigetBatchSize = 50;

    private const int MaxRedirects = 3;

    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";
    private static readonly XNamespace Apple = "http://apple.com/ns/ical/";
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Report = new("REPORT");

    private readonly HttpClient _httpClient;
    private readonly ILogger<CalDavCalendarClient> _logger;

    public CalDavCalendarClient(HttpClient httpClient, ILogger<CalDavCalendarClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // ---- Colecciones del home ----

    private static readonly XName[] CalendarListProps =
    [
        Dav + "resourcetype", Dav + "displayname", Apple + "calendar-color", CalendarServer + "getctag",
        Dav + "supported-report-set", CalDav + "supported-calendar-component-set",
    ];

    public async Task<CalDavResult<IReadOnlyList<RemoteCalendar>>> ListCalendarsAsync(
        Uri home, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken)
    {
        var answer = await PropfindAsync(home, credentials, CalendarListProps, depth: "1", cancellationToken);
        if (answer is not Answer.Ok ok)
        {
            return NotOk<IReadOnlyList<RemoteCalendar>>(answer);
        }

        if (TryParseXml(ok.Body) is null)
        {
            return Invalid<IReadOnlyList<RemoteCalendar>>("lista de calendarios no es XML");
        }

        var calendars = Responses(ok)
            .Where(r => IsEventCalendar(r.Element))
            .Select(r => new RemoteCalendar(
                r.Href.AbsoluteUri,
                Prop(r.Element, Dav + "displayname"),
                Prop(r.Element, Apple + "calendar-color"),
                Prop(r.Element, CalendarServer + "getctag"),
                SupportsSyncCollection(r.Element),
                SameCollection(r.Href, ok.Url)))
            .ToList();

        return new CalDavResult<IReadOnlyList<RemoteCalendar>>.Ok(calendars);
    }

    private static bool SupportsSyncCollection(XElement response) =>
        OkProps(response)
            .Select(p => p.Element(Dav + "supported-report-set"))
            .OfType<XElement>()
            .Any(set => set.Descendants(Dav + "sync-collection").Any());

    // ---- sync-collection ----

    public async Task<CalDavResult<SyncCollectionChanges>> SyncCollectionAsync(
        Uri calendar, CalendarCredentials.CalDav credentials, string? syncToken, CancellationToken cancellationToken)
    {
        var body = new XElement(
            Dav + "sync-collection",
            new XAttribute(XNamespace.Xmlns + "d", Dav),
            new XElement(Dav + "sync-token", syncToken ?? string.Empty),
            new XElement(Dav + "sync-level", "1"),
            new XElement(Dav + "prop", new XElement(Dav + "getetag"))).ToString();

        // RFC 6578, 3.2: el informe solo se define con `Depth: 0`.
        var answer = await SendAsync(Report, calendar, credentials, body, depth: "0", cancellationToken);
        if (answer is not Answer.Ok ok)
        {
            return NotOk<SyncCollectionChanges>(answer);
        }

        if (TryParseXml(ok.Body) is not { } document)
        {
            return Invalid<SyncCollectionChanges>("sync-collection no es XML");
        }

        var changed = new List<RemoteETag>();
        var removed = new List<string>();
        var truncated = false;

        foreach (var response in Responses(ok))
        {
            var status = (string?)response.Element.Element(Dav + "status");
            if (SameCollection(response.Href, ok.Url))
            {
                // `507` sobre la propia colección: el servidor cortó la lista (RFC 6578, 3.6).
                truncated |= status?.Contains(" 507 ", StringComparison.Ordinal) ?? false;
                continue;
            }

            if (status?.Contains(" 404 ", StringComparison.Ordinal) ?? false)
            {
                removed.Add(response.Href.AbsoluteUri);
                continue;
            }

            if (!IsCollection(response.Element))
            {
                changed.Add(new RemoteETag(response.Href.AbsoluteUri, Prop(response.Element, Dav + "getetag")));
            }
        }

        var token = document.Root?.Element(Dav + "sync-token")?.Value?.Trim();
        return new CalDavResult<SyncCollectionChanges>.Ok(
            new SyncCollectionChanges(changed, removed, string.IsNullOrEmpty(token) ? null : token, truncated));
    }

    // ---- calendar-multiget ----

    public async Task<CalDavResult<MultigetResult>> MultigetAsync(
        Uri calendar, CalendarCredentials.CalDav credentials, IReadOnlyList<string> hrefs, CancellationToken cancellationToken)
    {
        var resources = new List<RemoteResource>();
        var missing = new List<string>();

        foreach (var batch in hrefs.Chunk(MultigetBatchSize))
        {
            var result = await MultigetBatchAsync(calendar, credentials, batch, cancellationToken);
            if (result is not CalDavResult<MultigetResult>.Ok ok)
            {
                return result;
            }

            resources.AddRange(ok.Value.Resources);
            missing.AddRange(ok.Value.Missing);
        }

        return new CalDavResult<MultigetResult>.Ok(new MultigetResult(resources, missing));
    }

    /// <summary>
    /// Un lote. Si la respuesta pasa del tamaño máximo se parte en dos; un único recurso que no
    /// cabe se omite, contado en el registro, para que no impida copiar el resto del calendario.
    /// </summary>
    private async Task<CalDavResult<MultigetResult>> MultigetBatchAsync(
        Uri calendar, CalendarCredentials.CalDav credentials, IReadOnlyList<string> hrefs, CancellationToken cancellationToken)
    {
        var body = new XElement(
            CalDav + "calendar-multiget",
            new XAttribute(XNamespace.Xmlns + "d", Dav),
            new XAttribute(XNamespace.Xmlns + "c", CalDav),
            new XElement(Dav + "prop", new XElement(Dav + "getetag"), new XElement(CalDav + "calendar-data")),
            hrefs.Select(h => new XElement(Dav + "href", PathOf(h)))).ToString();

        var answer = await SendAsync(Report, calendar, credentials, body, depth: "1", cancellationToken);

        if (answer is Answer.TooLarge)
        {
            if (hrefs.Count == 1)
            {
                _logger.LogWarning("Un recurso CalDAV supera el tamaño máximo y se omite");
                return new CalDavResult<MultigetResult>.Ok(new MultigetResult([], []));
            }

            var half = hrefs.Count / 2;
            var first = await MultigetBatchAsync(calendar, credentials, hrefs.Take(half).ToList(), cancellationToken);
            if (first is not CalDavResult<MultigetResult>.Ok a)
            {
                return first;
            }

            var second = await MultigetBatchAsync(calendar, credentials, hrefs.Skip(half).ToList(), cancellationToken);
            return second is CalDavResult<MultigetResult>.Ok b
                ? new CalDavResult<MultigetResult>.Ok(new MultigetResult(
                    [.. a.Value.Resources, .. b.Value.Resources], [.. a.Value.Missing, .. b.Value.Missing]))
                : second;
        }

        if (answer is not Answer.Ok ok)
        {
            return NotOk<MultigetResult>(answer);
        }

        if (TryParseXml(ok.Body) is null)
        {
            return Invalid<MultigetResult>("multiget no es XML");
        }

        var resources = new List<RemoteResource>();
        var missing = new List<string>();
        foreach (var response in Responses(ok))
        {
            var ics = Prop(response.Element, CalDav + "calendar-data", trim: false);
            if (string.IsNullOrWhiteSpace(ics))
            {
                // Sin datos: un `404`, o un recurso que el servidor no sabe servir.
                missing.Add(response.Href.AbsoluteUri);
                continue;
            }

            resources.Add(new RemoteResource(response.Href.AbsoluteUri, Prop(response.Element, Dav + "getetag"), ics));
        }

        return new CalDavResult<MultigetResult>.Ok(new MultigetResult(resources, missing));
    }

    // ---- ETags ----

    public async Task<CalDavResult<IReadOnlyList<RemoteETag>>> ListETagsAsync(
        Uri calendar, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken)
    {
        var answer = await PropfindAsync(
            calendar, credentials, [Dav + "resourcetype", Dav + "getetag"], depth: "1", cancellationToken);
        if (answer is not Answer.Ok ok)
        {
            return NotOk<IReadOnlyList<RemoteETag>>(answer);
        }

        if (TryParseXml(ok.Body) is null)
        {
            return Invalid<IReadOnlyList<RemoteETag>>("lista de ETags no es XML");
        }

        var etags = Responses(ok)
            .Where(r => !SameCollection(r.Href, ok.Url) && !IsCollection(r.Element))
            .Select(r => new RemoteETag(r.Href.AbsoluteUri, Prop(r.Element, Dav + "getetag")))
            .ToList();

        return new CalDavResult<IReadOnlyList<RemoteETag>>.Ok(etags);
    }

    // ---- Descubrimiento ----

    public async Task<CalDavDiscoveryResult> DiscoverAsync(
        Uri url, string username, string password, CancellationToken cancellationToken = default)
    {
        var credentials = new CalendarCredentials.CalDav(username, password);

        // 1. La URL dada puede ser ya el home o un calendario, que dicen quién es su principal.
        var first = await PropfindAsync(url, credentials, CollectionProps, depth: "0", cancellationToken);
        if (first is Answer.Rejected)
        {
            return new CalDavDiscoveryResult.CredentialsRejected();
        }

        if (first is Answer.Ok ok && Responses(ok).FirstOrDefault() is { } self)
        {
            var principalFromUrl = Href(self.Element, Dav + "current-user-principal", self.Href);

            if (IsEventCalendar(self.Element))
            {
                // Un calendario: se sube a su home si el servidor deja; si no, el calendario hace
                // de home de uno solo.
                if (principalFromUrl is not null
                    && await FromPrincipalAsync(principalFromUrl, credentials, cancellationToken) is CalDavDiscoveryResult.Found home)
                {
                    return home;
                }

                return new CalDavDiscoveryResult.Found(self.Href.AbsoluteUri);
            }

            if (principalFromUrl is not null)
            {
                return await FromPrincipalAsync(principalFromUrl, credentials, cancellationToken);
            }
        }

        // 2. Si no, `/.well-known/caldav` del mismo host.
        var wellKnown = new Uri(url, "/.well-known/caldav");
        var discovered = await PropfindAsync(wellKnown, credentials, PrincipalProps, depth: "0", cancellationToken);

        if (discovered is Answer.Rejected)
        {
            return new CalDavDiscoveryResult.CredentialsRejected();
        }

        if (discovered is Answer.Ok found
            && Responses(found).Select(r => Href(r.Element, Dav + "current-user-principal", r.Href))
                .FirstOrDefault(h => h is not null) is { } principal)
        {
            return await FromPrincipalAsync(principal, credentials, cancellationToken);
        }

        // Si la URL dada ni siquiera respondió, es el servidor el que no está; si respondió, es
        // que no hay calendario que encontrar.
        return first is Answer.Failed or Answer.TooLarge
            ? new CalDavDiscoveryResult.Unreachable(first is Answer.Failed failed ? failed.Cause : "CalDAV: respuesta demasiado grande")
            : new CalDavDiscoveryResult.NoCalendar();
    }

    /// <summary>
    /// 3. Del principal, `calendar-home-set`, siempre que en el home haya al menos un calendario
    /// que admita VEVENT.
    /// </summary>
    private async Task<CalDavDiscoveryResult> FromPrincipalAsync(
        Uri principal, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken)
    {
        var answer = await PropfindAsync(principal, credentials, HomeProps, depth: "0", cancellationToken);
        switch (answer)
        {
            case Answer.Rejected:
                return new CalDavDiscoveryResult.CredentialsRejected();
            case Answer.Failed failed:
                return new CalDavDiscoveryResult.Unreachable(failed.Cause);
            case not Answer.Ok:
                return new CalDavDiscoveryResult.NoCalendar();
        }

        var principalResponse = Responses((Answer.Ok)answer).FirstOrDefault();
        if (principalResponse is null
            || Href(principalResponse.Element, CalDav + "calendar-home-set", principalResponse.Href) is not { } home)
        {
            return new CalDavDiscoveryResult.NoCalendar();
        }

        var collections = await ListCalendarsAsync(home, credentials, cancellationToken);
        return collections switch
        {
            CalDavResult<IReadOnlyList<RemoteCalendar>>.Rejected => new CalDavDiscoveryResult.CredentialsRejected(),
            CalDavResult<IReadOnlyList<RemoteCalendar>>.Failed failed => new CalDavDiscoveryResult.Unreachable(failed.Cause),
            CalDavResult<IReadOnlyList<RemoteCalendar>>.Ok { Value.Count: > 0 } => new CalDavDiscoveryResult.Found(home.AbsoluteUri),
            _ => new CalDavDiscoveryResult.NoCalendar(),
        };
    }

    private static readonly XName[] CollectionProps =
        [Dav + "resourcetype", Dav + "current-user-principal", CalDav + "supported-calendar-component-set"];

    private static readonly XName[] PrincipalProps = [Dav + "current-user-principal"];

    private static readonly XName[] HomeProps = [CalDav + "calendar-home-set"];

    // ---- XML ----

    private Task<Answer> PropfindAsync(
        Uri url, CalendarCredentials.CalDav credentials, XName[] props, string depth, CancellationToken cancellationToken)
    {
        var body = new XElement(
            Dav + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", Dav),
            new XAttribute(XNamespace.Xmlns + "c", CalDav),
            new XAttribute(XNamespace.Xmlns + "cs", CalendarServer),
            new XAttribute(XNamespace.Xmlns + "ical", Apple),
            new XElement(Dav + "prop", props.Select(p => new XElement(p)))).ToString();

        return SendAsync(Propfind, url, credentials, body, depth, cancellationToken);
    }

    /// <summary>
    /// Una colección de calendario cuyo `supported-calendar-component-set` incluye VEVENT. Sin esa
    /// propiedad, la colección admite cualquier componente (RFC 4791, 5.2.3).
    /// </summary>
    private static bool IsEventCalendar(XElement response)
    {
        var props = OkProps(response).ToList();
        var isCalendar = props.Any(p => p.Element(Dav + "resourcetype")?.Element(CalDav + "calendar") is not null);
        if (!isCalendar)
        {
            return false;
        }

        var components = props
            .Select(p => p.Element(CalDav + "supported-calendar-component-set"))
            .FirstOrDefault(s => s is not null);

        return components is null
            || components.Elements(CalDav + "comp").Any(c =>
                string.Equals((string?)c.Attribute("name"), "VEVENT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCollection(XElement response) =>
        OkProps(response).Any(p => p.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null);

    /// <summary>Las propiedades de los `propstat` con estado 200.</summary>
    private static IEnumerable<XElement> OkProps(XElement response) =>
        response.Elements(Dav + "propstat")
            .Where(ps => ((string?)ps.Element(Dav + "status"))?.Contains(" 200 ", StringComparison.Ordinal) ?? true)
            .Select(ps => ps.Element(Dav + "prop"))
            .OfType<XElement>();

    /// <summary>El texto de una propiedad con estado 200, o null si falta o está vacía.</summary>
    private static string? Prop(XElement response, XName property, bool trim = true)
    {
        var value = OkProps(response).Select(p => p.Element(property)?.Value).FirstOrDefault(v => v is not null);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return trim ? value.Trim() : value;
    }

    /// <summary>El `href` dentro de la propiedad, resuelto contra la URL de la respuesta.</summary>
    private static Uri? Href(XElement response, XName property, Uri baseUri)
    {
        var href = OkProps(response).Select(p => p.Element(property)?.Element(Dav + "href")?.Value).FirstOrDefault(h => h is not null);
        return string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(baseUri, href.Trim(), out var resolved) ? null : resolved;
    }

    /// <summary>La misma colección, con o sin barra final.</summary>
    private static bool SameCollection(Uri a, Uri b) =>
        string.Equals(a.AbsoluteUri.TrimEnd('/'), b.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>Lo que se manda en un `href`: la ruta, ya codificada.</summary>
    private static string PathOf(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href;

    private sealed record DavResponse(Uri Href, XElement Element);

    private static IEnumerable<DavResponse> Responses(Answer.Ok answer)
    {
        if (TryParseXml(answer.Body) is not { } document)
        {
            yield break;
        }

        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value?.Trim();
            if (!string.IsNullOrEmpty(href) && Uri.TryCreate(answer.Url, href, out var resolved))
            {
                yield return new DavResponse(resolved, response);
            }
        }
    }

    private static XDocument? TryParseXml(string text)
    {
        try
        {
            return XDocument.Parse(text);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // ---- HTTP ----

    private abstract record Answer
    {
        public sealed record Ok(Uri Url, string Body) : Answer;

        public sealed record Rejected : Answer;

        public sealed record TokenInvalid : Answer;

        public sealed record TooLarge : Answer;

        public sealed record Failed(string Category, string Cause) : Answer;
    }

    private static CalDavResult<T> NotOk<T>(Answer answer) => answer switch
    {
        Answer.Rejected => new CalDavResult<T>.Rejected(),
        Answer.TokenInvalid => new CalDavResult<T>.TokenInvalid(),
        Answer.TooLarge => new CalDavResult<T>.Failed(CalendarSyncErrors.InvalidResponse, "CalDAV: respuesta demasiado grande"),
        Answer.Failed failed => new CalDavResult<T>.Failed(failed.Category, failed.Cause),
        _ => new CalDavResult<T>.Failed(CalendarSyncErrors.InvalidResponse, "CalDAV: respuesta inesperada"),
    };

    private static CalDavResult<T> Invalid<T>(string cause) =>
        new CalDavResult<T>.Failed(CalendarSyncErrors.InvalidResponse, $"CalDAV: {cause}");

    /// <summary>
    /// Envía la petición siguiendo como mucho <see cref="MaxRedirects"/> redirecciones, solo dentro
    /// del mismo host y siempre por HTTPS, y lee la respuesta con un tamaño máximo. Nunca lanza por
    /// un fallo del servidor ni registra su cuerpo.
    /// </summary>
    private async Task<Answer> SendAsync(
        HttpMethod method, Uri url, CalendarCredentials.CalDav credentials, string body, string depth,
        CancellationToken cancellationToken)
    {
        var current = url;

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (current.Scheme != Uri.UriSchemeHttps)
                {
                    return new Answer.Failed(CalendarSyncErrors.Unreachable, "CalDAV sin HTTPS");
                }

                using var request = new HttpRequestMessage(method, current)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/xml"),
                };
                request.Headers.Add("Depth", depth);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}")));

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (IsRedirect(response.StatusCode))
                {
                    if (response.Headers.Location is not { } location)
                    {
                        return new Answer.Failed(CalendarSyncErrors.ServerError, $"CalDAV: HTTP {(int)response.StatusCode} sin Location");
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!string.Equals(next.Host, current.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        return new Answer.Failed(CalendarSyncErrors.ServerError, "CalDAV: redirección a otro host");
                    }

                    current = next;
                    continue;
                }

                // Un token caducado llega como `403` o `409` con la precondición `valid-sync-token`
                // (RFC 6578, 3.2); solo se mira si está, sin guardar ni registrar el cuerpo.
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict
                    && await ReadLimitedAsync(response, cancellationToken) is { } error
                    && error.Contains("valid-sync-token", StringComparison.Ordinal))
                {
                    return new Answer.TokenInvalid();
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return new Answer.Rejected();
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new Answer.Failed(CalendarSyncErrors.ServerError, $"CalDAV: HTTP {(int)response.StatusCode}");
                }

                var text = await ReadLimitedAsync(response, cancellationToken);
                return text is null ? new Answer.TooLarge() : new Answer.Ok(current, text);
            }

            return new Answer.Failed(CalendarSyncErrors.ServerError, "CalDAV: demasiadas redirecciones");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Answer.Failed(CalendarSyncErrors.Unreachable, "CalDAV no respondió a tiempo");
        }
        catch (HttpRequestException exception)
        {
            // El tipo de fallo y nunca el mensaje, que puede llevar la dirección.
            return new Answer.Failed(CalendarSyncErrors.Unreachable, $"CalDAV inalcanzable ({exception.HttpRequestError})");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    private static async Task<string?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
