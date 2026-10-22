using Ical.Net;
using Ical.Net.DataTypes;
using Microsoft.Extensions.Logging;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
// Ical.Net tiene su propio CalendarEvent; el nuestro es el del dominio, así que el suyo es un VEVENT.
using VEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Lee un calendario CalDAV y lo descubre al conectar (calendar-integration design.md Decisión 4).
/// Las peticiones son nuestras porque son pocas y fijas (PROPFIND y un REPORT); el iCalendar lo
/// parsea Ical.Net, que también expande en local las recurrencias si el servidor ignora `expand`.
///
/// La defensa contra SSRF está en el handler del <see cref="HttpClient"/> (design.md Decisión 5).
/// Aquí se exige HTTPS, no se siguen redirecciones a otro host y se limita el tamaño de las
/// respuestas.
/// </summary>
public sealed class CalDavCalendarClient : ICalendarProviderClient, ICalDavDiscovery
{
    /// <summary>Tamaño máximo de una respuesta; un calendario de un mes cabe de sobra.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    private const int MaxRedirects = 3;

    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Report = new("REPORT");

    private readonly HttpClient _httpClient;
    private readonly ILogger<CalDavCalendarClient> _logger;

    public CalDavCalendarClient(HttpClient httpClient, ILogger<CalDavCalendarClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public CalendarProvider Provider => CalendarProvider.CalDav;

    // ---- Lectura ----

    public async Task<CalendarProviderRead> ReadAsync(CalendarProviderRequest request, CancellationToken cancellationToken)
    {
        if (request.Credentials is not CalendarCredentials.CalDav credentials)
        {
            return new CalendarProviderRead(new CalendarReadResult.Unavailable("credenciales de otro proveedor"));
        }

        if (!Uri.TryCreate(request.CalendarRef, UriKind.Absolute, out var calendarUrl))
        {
            return new CalendarProviderRead(new CalendarReadResult.Unavailable("URL de calendario no válida"));
        }

        var answer = await SendAsync(
            Report, calendarUrl, credentials, CalendarQuery(request.FromUtc, request.ToUtc), depth: "1", cancellationToken);

        CalendarReadResult result = answer switch
        {
            Answer.Rejected => new CalendarReadResult.NeedsReconnect(),
            Answer.Failed failed => new CalendarReadResult.Unavailable(failed.Cause),
            Answer.Ok ok => ParseEvents(ok.Body, credentials.Username, request),
            _ => new CalendarReadResult.Unavailable("respuesta inesperada"),
        };

        return new CalendarProviderRead(result);
    }

    /// <summary>
    /// `calendar-query` con `time-range` y `expand`, para que el servidor devuelva las ocurrencias
    /// de las recurrencias en lugar de la regla.
    /// </summary>
    private static string CalendarQuery(DateTime fromUtc, DateTime toUtc)
    {
        var range = new[] { new XAttribute("start", Stamp(fromUtc)), new XAttribute("end", Stamp(toUtc)) };

        return new XElement(
            CalDav + "calendar-query",
            new XAttribute(XNamespace.Xmlns + "d", Dav),
            new XAttribute(XNamespace.Xmlns + "c", CalDav),
            new XElement(
                Dav + "prop",
                new XElement(CalDav + "calendar-data", new XElement(CalDav + "expand", range))),
            new XElement(
                CalDav + "filter",
                new XElement(
                    CalDav + "comp-filter",
                    new XAttribute("name", "VCALENDAR"),
                    new XElement(
                        CalDav + "comp-filter",
                        new XAttribute("name", "VEVENT"),
                        new XElement(CalDav + "time-range", range.Select(a => new XAttribute(a))))))).ToString();
    }

    private CalendarReadResult ParseEvents(string multiStatus, string username, CalendarProviderRequest request)
    {
        if (TryParseXml(multiStatus) is not { } document)
        {
            return new CalendarReadResult.Unavailable("respuesta CalDAV no es XML");
        }

        var events = new List<CalendarEvent>();
        var unparseable = 0;

        foreach (var data in document.Descendants(CalDav + "calendar-data"))
        {
            if (string.IsNullOrWhiteSpace(data.Value))
            {
                continue;
            }

            try
            {
                events.AddRange(Occurrences(data.Value, username, request.FromUtc, request.ToUtc));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Un elemento ilegible no cuesta el resto de eventos.
                unparseable++;
            }
        }

        if (unparseable > 0)
        {
            _logger.LogWarning("{Count} elementos CalDAV no se pudieron leer y se omitieron", unparseable);
        }

        return new CalendarReadResult.Ok(
            [.. events.OrderBy(e => e.StartUtc).ThenBy(e => e.EndUtc).Take(request.MaxEvents)]);
    }

    /// <summary>
    /// Las ocurrencias de un objeto iCalendar dentro del intervalo. Si el servidor ya expandió,
    /// cada VEVENT es una ocurrencia; si no, Ical.Net expande la regla (con sus excepciones y
    /// ocurrencias movidas) en local.
    /// </summary>
    internal static IEnumerable<CalendarEvent> Occurrences(string iCalendar, string username, DateTime fromUtc, DateTime toUtc)
    {
        var calendar = Ical.Net.Calendar.Load(iCalendar);
        if (calendar is null)
        {
            yield break;
        }

        var occurrences = calendar
            .GetOccurrences<VEvent>(new CalDateTime(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)), null)
            .TakeWhileBefore(new CalDateTime(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)));

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Source is not VEvent source || IsCancelled(source) || DeclinedBy(source, username))
            {
                continue;
            }

            var mapped = Map(source, occurrence.Period);
            if (mapped is not null && mapped.StartUtc < toUtc && mapped.EndUtc > fromUtc)
            {
                yield return mapped;
            }
        }
    }

    private static bool IsCancelled(VEvent source) =>
        string.Equals(source.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rechazado por el propio usuario: un ATTENDEE con su dirección y `PARTSTAT=DECLINED`. Si el
    /// usuario no coincide con ningún asistente, el evento se muestra (design.md, Riesgos).
    /// </summary>
    private static bool DeclinedBy(VEvent source, string username) =>
        source.Attendees.Any(a =>
            string.Equals(a.ParticipationStatus, "DECLINED", StringComparison.OrdinalIgnoreCase)
            && a.Value is { } address
            && string.Equals(StripMailto(address.OriginalString), username.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string StripMailto(string address) =>
        address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? address[7..].Trim() : address.Trim();

    private static CalendarEvent? Map(VEvent source, Period period)
    {
        var start = period.StartTime;
        if (start is null)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(source.Summary) ? "(sin título)" : source.Summary.Trim();
        var location = string.IsNullOrWhiteSpace(source.Location) ? null : source.Location.Trim();
        var description = string.IsNullOrWhiteSpace(source.Description) ? null : source.Description.Trim();
        var end = period.EffectiveEndTime;

        // `VALUE=DATE`: el día anotado, sin zona horaria.
        if (!start.HasTime)
        {
            var days = end is { HasTime: false } ? end.Date.DayNumber - start.Date.DayNumber : 1;
            return CalendarEvent.AllDay(title, start.Date, days, location, description);
        }

        var startUtc = DateTime.SpecifyKind(start.AsUtc, DateTimeKind.Utc);
        var endUtc = end is { HasTime: true } ? DateTime.SpecifyKind(end.AsUtc, DateTimeKind.Utc) : startUtc;

        return new CalendarEvent(title, startUtc, endUtc, null, location, description);
    }

    // ---- Descubrimiento ----

    public async Task<CalDavDiscoveryResult> DiscoverAsync(
        Uri url, string username, string password, CancellationToken cancellationToken = default)
    {
        var credentials = new CalendarCredentials.CalDav(username, password);

        // 1. La URL dada puede ser ya la colección.
        var first = await PropfindAsync(url, credentials, CollectionProps, depth: "0", cancellationToken);
        if (first is Answer.Rejected)
        {
            return new CalDavDiscoveryResult.CredentialsRejected();
        }

        if (first is Answer.Ok ok && Responses(ok).FirstOrDefault() is { } self)
        {
            if (IsEventCalendar(self.Element))
            {
                return new CalDavDiscoveryResult.Found(self.Href.AbsoluteUri);
            }

            if (Href(self.Element, Dav + "current-user-principal", self.Href) is { } principalFromUrl)
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
        return first is Answer.Failed failed
            ? new CalDavDiscoveryResult.Unreachable(failed.Cause)
            : new CalDavDiscoveryResult.NoCalendar();
    }

    /// <summary>
    /// 3. Del principal, `calendar-home-set`; el calendario por defecto si el servidor lo expone o,
    /// si no, la primera colección que admite VEVENT.
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
        }

        var principalResponse = Responses((Answer.Ok)answer).FirstOrDefault();
        if (principalResponse is null)
        {
            return new CalDavDiscoveryResult.NoCalendar();
        }

        if (Href(principalResponse.Element, CalDav + "schedule-default-calendar-URL", principalResponse.Href) is { } byDefault)
        {
            return new CalDavDiscoveryResult.Found(byDefault.AbsoluteUri);
        }

        if (Href(principalResponse.Element, CalDav + "calendar-home-set", principalResponse.Href) is not { } home)
        {
            return new CalDavDiscoveryResult.NoCalendar();
        }

        var collections = await PropfindAsync(home, credentials, CollectionProps, depth: "1", cancellationToken);
        return collections switch
        {
            Answer.Rejected => new CalDavDiscoveryResult.CredentialsRejected(),
            Answer.Failed failed => new CalDavDiscoveryResult.Unreachable(failed.Cause),
            Answer.Ok ok => Responses(ok).FirstOrDefault(r => IsEventCalendar(r.Element)) is { } calendar
                ? new CalDavDiscoveryResult.Found(calendar.Href.AbsoluteUri)
                : new CalDavDiscoveryResult.NoCalendar(),
            _ => new CalDavDiscoveryResult.NoCalendar(),
        };
    }

    private static readonly XName[] CollectionProps =
        [Dav + "resourcetype", Dav + "current-user-principal", CalDav + "supported-calendar-component-set"];

    private static readonly XName[] PrincipalProps = [Dav + "current-user-principal"];

    private static readonly XName[] HomeProps =
        [CalDav + "calendar-home-set", CalDav + "schedule-default-calendar-URL"];

    private Task<Answer> PropfindAsync(
        Uri url, CalendarCredentials.CalDav credentials, XName[] props, string depth, CancellationToken cancellationToken)
    {
        var body = new XElement(
            Dav + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", Dav),
            new XAttribute(XNamespace.Xmlns + "c", CalDav),
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

    /// <summary>Las propiedades de los `propstat` con estado 200.</summary>
    private static IEnumerable<XElement> OkProps(XElement response) =>
        response.Elements(Dav + "propstat")
            .Where(ps => ((string?)ps.Element(Dav + "status"))?.Contains(" 200 ", StringComparison.Ordinal) ?? true)
            .Select(ps => ps.Element(Dav + "prop"))
            .OfType<XElement>();

    /// <summary>El `href` dentro de la propiedad, resuelto contra la URL de la respuesta.</summary>
    private static Uri? Href(XElement response, XName property, Uri baseUri)
    {
        var href = OkProps(response).Select(p => p.Element(property)?.Element(Dav + "href")?.Value).FirstOrDefault(h => h is not null);
        return string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(baseUri, href.Trim(), out var resolved) ? null : resolved;
    }

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

    // ---- HTTP ----

    private abstract record Answer
    {
        public sealed record Ok(Uri Url, string Body) : Answer;

        public sealed record Rejected : Answer;

        public sealed record Failed(string Cause) : Answer;
    }

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
                    return new Answer.Failed("CalDAV sin HTTPS");
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
                        return new Answer.Failed($"CalDAV: HTTP {(int)response.StatusCode} sin Location");
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!string.Equals(next.Host, current.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        return new Answer.Failed("CalDAV: redirección a otro host");
                    }

                    current = next;
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return new Answer.Rejected();
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new Answer.Failed($"CalDAV: HTTP {(int)response.StatusCode}");
                }

                var text = await ReadLimitedAsync(response, cancellationToken);
                return text is null
                    ? new Answer.Failed("CalDAV: respuesta demasiado grande")
                    : new Answer.Ok(current, text);
            }

            return new Answer.Failed("CalDAV: demasiadas redirecciones");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Answer.Failed("CalDAV no respondió a tiempo");
        }
        catch (HttpRequestException exception)
        {
            // El tipo de fallo y nunca el mensaje, que puede llevar la dirección.
            return new Answer.Failed($"CalDAV inalcanzable ({exception.HttpRequestError})");
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

    /// <summary>El formato básico de iCalendar que usa `time-range`: 20261007T000000Z.</summary>
    private static string Stamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
