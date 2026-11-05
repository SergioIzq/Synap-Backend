namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// Respuestas grabadas de Radicale y Nextcloud a las peticiones de la sincronización
/// (calendar-store-and-view tarea 1.3), recortadas a lo que se usa y con datos cambiados. Radicale
/// manda los hrefs como rutas y `getctag`; Nextcloud (sabre/dav) añade su propio espacio de nombres
/// y responde el token caducado con un `403` y la precondición `valid-sync-token`.
/// </summary>
internal static class CalDavSyncRecordings
{
    // ---- Radicale ----

    public const string RadicaleHome = "https://radicale.example.com/sergio/";
    public const string RadicalePersonal = "https://radicale.example.com/sergio/personal/";
    public const string RadicaleWork = "https://radicale.example.com/sergio/trabajo/";

    /// <summary>El home: dos calendarios de eventos y una lista de tareas.</summary>
    public const string RadicaleHomeListing = """
        <?xml version='1.0' encoding='utf-8'?>
        <multistatus xmlns="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:CS="http://calendarserver.org/ns/" xmlns:ICAL="http://apple.com/ns/ical/">
          <response>
            <href>/sergio/</href>
            <propstat>
              <prop><resourcetype><principal /><collection /></resourcetype></prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
            <propstat>
              <prop><displayname /><ICAL:calendar-color /><CS:getctag /><C:supported-calendar-component-set /></prop>
              <status>HTTP/1.1 404 Not Found</status>
            </propstat>
          </response>
          <response>
            <href>/sergio/personal/</href>
            <propstat>
              <prop>
                <resourcetype><C:calendar /><collection /></resourcetype>
                <displayname>Personal</displayname>
                <ICAL:calendar-color>#ff0000ff</ICAL:calendar-color>
                <CS:getctag>"6c1b8e6e5fd2a0e1"</CS:getctag>
                <supported-report-set>
                  <supported-report><report><expand-property /></report></supported-report>
                  <supported-report><report><sync-collection /></report></supported-report>
                  <supported-report><report><C:calendar-multiget /></report></supported-report>
                  <supported-report><report><C:calendar-query /></report></supported-report>
                </supported-report-set>
                <C:supported-calendar-component-set><C:comp name="VEVENT" /></C:supported-calendar-component-set>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
          </response>
          <response>
            <href>/sergio/trabajo/</href>
            <propstat>
              <prop>
                <resourcetype><C:calendar /><collection /></resourcetype>
                <displayname>Trabajo</displayname>
                <CS:getctag>"aa01"</CS:getctag>
                <supported-report-set>
                  <supported-report><report><sync-collection /></report></supported-report>
                </supported-report-set>
                <C:supported-calendar-component-set><C:comp name="VEVENT" /><C:comp name="VTODO" /></C:supported-calendar-component-set>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
            <propstat>
              <prop><ICAL:calendar-color /></prop>
              <status>HTTP/1.1 404 Not Found</status>
            </propstat>
          </response>
          <response>
            <href>/sergio/tareas/</href>
            <propstat>
              <prop>
                <resourcetype><C:calendar /><collection /></resourcetype>
                <displayname>Tareas</displayname>
                <C:supported-calendar-component-set><C:comp name="VTODO" /></C:supported-calendar-component-set>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
          </response>
        </multistatus>
        """;

    /// <summary>Sincronización completa: dos eventos y el token nuevo.</summary>
    public const string RadicaleInitialSync = """
        <?xml version='1.0' encoding='utf-8'?>
        <multistatus xmlns="DAV:">
          <response>
            <href>/sergio/personal/cena.ics</href>
            <propstat><prop><getetag>"e-cena-1"</getetag></prop><status>HTTP/1.1 200 OK</status></propstat>
          </response>
          <response>
            <href>/sergio/personal/gym%20lunes.ics</href>
            <propstat><prop><getetag>"e-gym-1"</getetag></prop><status>HTTP/1.1 200 OK</status></propstat>
          </response>
          <sync-token>http://radicale.org/ns/sync/9d1e5b3a</sync-token>
        </multistatus>
        """;

    /// <summary>Incremental: uno cambiado y uno borrado (`404`).</summary>
    public const string RadicaleIncrementalSync = """
        <?xml version='1.0' encoding='utf-8'?>
        <multistatus xmlns="DAV:">
          <response>
            <href>/sergio/personal/cena.ics</href>
            <propstat><prop><getetag>"e-cena-2"</getetag></prop><status>HTTP/1.1 200 OK</status></propstat>
          </response>
          <response>
            <href>/sergio/personal/gym%20lunes.ics</href>
            <status>HTTP/1.1 404 Not Found</status>
          </response>
          <sync-token>http://radicale.org/ns/sync/0f77c2d4</sync-token>
        </multistatus>
        """;

    /// <summary>Radicale responde el token desconocido con `403` y la precondición.</summary>
    public const string RadicaleInvalidToken = """
        <?xml version='1.0' encoding='utf-8'?>
        <error xmlns="DAV:"><valid-sync-token /></error>
        """;

    public const string RadicaleMultiget = """
        <?xml version='1.0' encoding='utf-8'?>
        <multistatus xmlns="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
          <response>
            <href>/sergio/personal/cena.ics</href>
            <propstat>
              <prop>
                <getetag>"e-cena-2"</getetag>
                <C:calendar-data>BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//test//EN
        BEGIN:VEVENT
        UID:cena
        DTSTAMP:20261001T120000Z
        DTSTART:20261114T190000Z
        DTEND:20261114T210000Z
        SUMMARY:Cena con Marta
        END:VEVENT
        END:VCALENDAR
        </C:calendar-data>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
          </response>
          <response>
            <href>/sergio/personal/borrado.ics</href>
            <status>HTTP/1.1 404 Not Found</status>
          </response>
        </multistatus>
        """;

    // ---- Nextcloud ----

    public const string NextcloudHome = "https://nube.example.com/remote.php/dav/calendars/sergio/";
    public const string NextcloudPersonal = "https://nube.example.com/remote.php/dav/calendars/sergio/personal/";

    /// <summary>
    /// El home de Nextcloud: el calendario personal, la bandeja de entrada y la papelera, que no
    /// son calendarios de eventos.
    /// </summary>
    public const string NextcloudHomeListing = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:s="http://sabredav.org/ns" xmlns:cal="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns" xmlns:x1="http://apple.com/ns/ical/">
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype><d:collection/><cal:calendar/></d:resourcetype>
                <d:displayname>Personal</d:displayname>
                <x1:calendar-color>#0082C9</x1:calendar-color>
                <cs:getctag>http://sabre.io/ns/sync/42</cs:getctag>
                <d:supported-report-set>
                  <d:supported-report><d:report><d:expand-property/></d:report></d:supported-report>
                  <d:supported-report><d:report><d:sync-collection/></d:report></d:supported-report>
                  <d:supported-report><d:report><cal:calendar-multiget/></d:report></d:supported-report>
                </d:supported-report-set>
                <cal:supported-calendar-component-set><cal:comp name="VEVENT"/></cal:supported-calendar-component-set>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/inbox/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/><cal:schedule-inbox/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/trashbin/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/><nc:trash-bin/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    /// <summary>Incremental con un borrado; el propio calendario no cuenta como cambio.</summary>
    public const string NextcloudIncrementalSync = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:s="http://sabredav.org/ns" xmlns:cal="urn:ietf:params:xml:ns:caldav">
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/sprint.ics</d:href>
            <d:propstat><d:prop><d:getetag>&quot;8a1f&quot;</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/viejo.ics</d:href>
            <d:status>HTTP/1.1 404 Not Found</d:status>
          </d:response>
          <d:sync-token>http://sabre.io/ns/sync/43</d:sync-token>
        </d:multistatus>
        """;

    /// <summary>sabre/dav ante un token desconocido.</summary>
    public const string NextcloudInvalidToken = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:error xmlns:d="DAV:" xmlns:s="http://sabredav.org/ns">
          <d:valid-sync-token/>
          <s:exception>Sabre\DAV\Exception\InvalidSyncToken</s:exception>
          <s:message>Invalid or unknown sync token</s:message>
        </d:error>
        """;

    /// <summary>Lista truncada: el `507` sobre el propio calendario.</summary>
    public const string NextcloudTruncatedSync = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/sprint.ics</d:href>
            <d:propstat><d:prop><d:getetag>"8a1f"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/</d:href>
            <d:status>HTTP/1.1 507 Insufficient Storage</d:status>
          </d:response>
          <d:sync-token>http://sabre.io/ns/sync/44</d:sync-token>
        </d:multistatus>
        """;

    /// <summary>`PROPFIND Depth: 1` de `getetag` sobre el calendario.</summary>
    public const string NextcloudETags = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:cal="urn:ietf:params:xml:ns:caldav">
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/><cal:calendar/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
            <d:propstat><d:prop><d:getetag/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/sprint.ics</d:href>
            <d:propstat><d:prop><d:resourcetype/><d:getetag>"8a1f"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/cumple.ics</d:href>
            <d:propstat><d:prop><d:resourcetype/><d:getetag>"77c0"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    /// <summary>El principal de Nextcloud, para subir desde un calendario a su home.</summary>
    public const string NextcloudPrincipal = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:cal="urn:ietf:params:xml:ns:caldav">
          <d:response>
            <d:href>/remote.php/dav/principals/users/sergio/</d:href>
            <d:propstat>
              <d:prop><cal:calendar-home-set><d:href>/remote.php/dav/calendars/sergio/</d:href></cal:calendar-home-set></d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;
}
