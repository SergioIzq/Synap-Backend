namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// Respuestas grabadas de servidores CalDAV reales (recortadas a lo que se usa y con datos
/// cambiados). Cada servidor tiene sus manías: iCloud responde `expand` y usa TZID con VTIMEZONE
/// embebido; Fastmail expone `schedule-default-calendar-URL`; Nextcloud no siempre expande.
/// </summary>
internal static class CalDavRecordings
{
    // ---- iCloud ----

    public const string ICloudHost = "https://caldav.icloud.com";

    public const string ICloudWellKnownPrincipal = """
        <?xml version="1.0" encoding="UTF-8"?>
        <multistatus xmlns="DAV:">
          <response>
            <href>/</href>
            <propstat>
              <prop><current-user-principal><href>/123456789/principal/</href></current-user-principal></prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
          </response>
        </multistatus>
        """;

    public const string ICloudRootNotCalendar = """
        <?xml version="1.0" encoding="UTF-8"?>
        <multistatus xmlns="DAV:">
          <response>
            <href>/</href>
            <propstat><prop><resourcetype><collection/></resourcetype></prop><status>HTTP/1.1 200 OK</status></propstat>
            <propstat><prop><current-user-principal/></prop><status>HTTP/1.1 404 Not Found</status></propstat>
          </response>
        </multistatus>
        """;

    public const string ICloudPrincipal = """
        <?xml version="1.0" encoding="UTF-8"?>
        <multistatus xmlns="DAV:">
          <response>
            <href>/123456789/principal/</href>
            <propstat>
              <prop>
                <calendar-home-set xmlns="urn:ietf:params:xml:ns:caldav"><href xmlns="DAV:">https://p42-caldav.icloud.com:443/123456789/calendars/</href></calendar-home-set>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
            <propstat>
              <prop><schedule-default-calendar-URL xmlns="urn:ietf:params:xml:ns:caldav"/></prop>
              <status>HTTP/1.1 404 Not Found</status>
            </propstat>
          </response>
        </multistatus>
        """;

    /// <summary>El home: la bandeja de entrada, una lista de recordatorios (VTODO) y el calendario.</summary>
    public const string ICloudHome = """
        <?xml version="1.0" encoding="UTF-8"?>
        <multistatus xmlns="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
          <response>
            <href>/123456789/calendars/</href>
            <propstat><prop><resourcetype><collection/></resourcetype></prop><status>HTTP/1.1 200 OK</status></propstat>
          </response>
          <response>
            <href>/123456789/calendars/inbox/</href>
            <propstat><prop><resourcetype><collection/><C:schedule-inbox/></resourcetype></prop><status>HTTP/1.1 200 OK</status></propstat>
          </response>
          <response>
            <href>/123456789/calendars/tasks/</href>
            <propstat>
              <prop>
                <resourcetype><collection/><C:calendar/></resourcetype>
                <C:supported-calendar-component-set><C:comp name="VTODO"/></C:supported-calendar-component-set>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
          </response>
          <response>
            <href>/123456789/calendars/home/</href>
            <propstat>
              <prop>
                <resourcetype><collection/><C:calendar/></resourcetype>
                <C:supported-calendar-component-set><C:comp name="VEVENT"/></C:supported-calendar-component-set>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
          </response>
        </multistatus>
        """;

    /// <summary>`REPORT` con `expand` respondido: ocurrencias con TZID y su VTIMEZONE.</summary>
    public const string ICloudReport = """
        <?xml version="1.0" encoding="UTF-8"?>
        <multistatus xmlns="DAV:">
          <response>
            <href>/123456789/calendars/home/abc.ics</href>
            <propstat>
              <prop>
                <calendar-data xmlns="urn:ietf:params:xml:ns:caldav">BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Apple Inc.//iCloud//EN
        BEGIN:VTIMEZONE
        TZID:America/Mexico_City
        BEGIN:STANDARD
        DTSTART:19701025T020000
        TZOFFSETFROM:-0500
        TZOFFSETTO:-0600
        TZNAME:CST
        END:STANDARD
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:abc
        DTSTAMP:20261001T120000Z
        DTSTART;TZID=America/Mexico_City:20261113T180000
        DTEND;TZID=America/Mexico_City:20261113T190000
        SUMMARY:Cena con Marta
        LOCATION:Roma Norte
        DESCRIPTION:Llevar el libro\, y el regalo
        END:VEVENT
        END:VCALENDAR
        </calendar-data>
              </prop>
              <status>HTTP/1.1 200 OK</status>
            </propstat>
          </response>
        </multistatus>
        """;

    // ---- Fastmail ----

    public const string FastmailHost = "https://caldav.fastmail.com";

    public const string FastmailRoot = """
        <?xml version="1.0" encoding="UTF-8"?>
        <D:multistatus xmlns:D="DAV:">
          <D:response>
            <D:href>/dav/</D:href>
            <D:propstat>
              <D:prop>
                <D:resourcetype><D:collection/></D:resourcetype>
                <D:current-user-principal><D:href>/dav/principals/user/sergio@fastmail.com/</D:href></D:current-user-principal>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
        </D:multistatus>
        """;

    public const string FastmailPrincipal = """
        <?xml version="1.0" encoding="UTF-8"?>
        <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
          <D:response>
            <D:href>/dav/principals/user/sergio@fastmail.com/</D:href>
            <D:propstat>
              <D:prop>
                <C:calendar-home-set><D:href>/dav/calendars/user/sergio@fastmail.com/</D:href></C:calendar-home-set>
                <C:schedule-default-calendar-URL><D:href>/dav/calendars/user/sergio@fastmail.com/Default/</D:href></C:schedule-default-calendar-URL>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
        </D:multistatus>
        """;

    /// <summary>Un evento de todo el día de dos días, y uno cancelado.</summary>
    public const string FastmailReport = """
        <?xml version="1.0" encoding="UTF-8"?>
        <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
          <D:response>
            <D:href>/dav/calendars/user/sergio@fastmail.com/Default/d1.ics</D:href>
            <D:propstat>
              <D:prop>
                <C:calendar-data>BEGIN:VCALENDAR&#13;
        VERSION:2.0&#13;
        PRODID:-//CyrusIMAP.org/Cyrus 3.9//EN&#13;
        BEGIN:VEVENT&#13;
        UID:d1&#13;
        DTSTAMP:20261001T120000Z&#13;
        DTSTART;VALUE=DATE:20261114&#13;
        DTEND;VALUE=DATE:20261116&#13;
        SUMMARY:Escapada a Toledo&#13;
        END:VEVENT&#13;
        END:VCALENDAR&#13;
        </C:calendar-data>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
          <D:response>
            <D:href>/dav/calendars/user/sergio@fastmail.com/Default/c1.ics</D:href>
            <D:propstat>
              <D:prop>
                <C:calendar-data>BEGIN:VCALENDAR&#13;
        VERSION:2.0&#13;
        PRODID:-//CyrusIMAP.org/Cyrus 3.9//EN&#13;
        BEGIN:VEVENT&#13;
        UID:c1&#13;
        DTSTAMP:20261001T120000Z&#13;
        DTSTART:20261114T100000Z&#13;
        DTEND:20261114T110000Z&#13;
        STATUS:CANCELLED&#13;
        SUMMARY:Retro cancelada&#13;
        END:VEVENT&#13;
        END:VCALENDAR&#13;
        </C:calendar-data>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
        </D:multistatus>
        """;

    // ---- Nextcloud ----

    public const string NextcloudCalendarUrl = "https://nube.example.com/remote.php/dav/calendars/sergio/personal/";

    public const string NextcloudCalendarItself = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:s="http://sabredav.org/ns" xmlns:cal="urn:ietf:params:xml:ns:caldav">
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype><d:collection/><cal:calendar/></d:resourcetype>
                <cal:supported-calendar-component-set><cal:comp name="VEVENT"/><cal:comp name="VTODO"/></cal:supported-calendar-component-set>
                <d:current-user-principal><d:href>/remote.php/dav/principals/users/sergio/</d:href></d:current-user-principal>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    /// <summary>
    /// Sin expandir: la regla semanal con una excepción movida, y una invitación que el usuario
    /// rechazó. Se expande en local.
    /// </summary>
    public const string NextcloudReportNotExpanded = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:cal="urn:ietf:params:xml:ns:caldav">
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/sprint.ics</d:href>
            <d:propstat>
              <d:prop>
                <cal:calendar-data>BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Sabre//Sabre VObject 4.5.4//EN
        BEGIN:VTIMEZONE
        TZID:Europe/Madrid
        BEGIN:DAYLIGHT
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0200
        TZNAME:CEST
        DTSTART:19700329T020000
        RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU
        END:DAYLIGHT
        BEGIN:STANDARD
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        TZNAME:CET
        DTSTART:19701025T030000
        RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU
        END:STANDARD
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:sprint
        DTSTAMP:20260901T120000Z
        DTSTART;TZID=Europe/Madrid:20260907T090000
        DTEND;TZID=Europe/Madrid:20260907T093000
        RRULE:FREQ=WEEKLY;BYDAY=MO
        SUMMARY:Reunión de sprint
        END:VEVENT
        BEGIN:VEVENT
        UID:sprint
        DTSTAMP:20260901T120000Z
        RECURRENCE-ID;TZID=Europe/Madrid:20261019T090000
        DTSTART;TZID=Europe/Madrid:20261019T100000
        DTEND;TZID=Europe/Madrid:20261019T103000
        SUMMARY:Reunión de sprint
        END:VEVENT
        END:VCALENDAR
        </cal:calendar-data>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/calendars/sergio/personal/invite.ics</d:href>
            <d:propstat>
              <d:prop>
                <cal:calendar-data>BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Sabre//Sabre VObject 4.5.4//EN
        BEGIN:VEVENT
        UID:invite
        DTSTAMP:20260901T120000Z
        DTSTART:20261020T120000Z
        DTEND:20261020T130000Z
        SUMMARY:Comida de equipo
        ORGANIZER:mailto:ana@example.com
        ATTENDEE;PARTSTAT=ACCEPTED:mailto:ana@example.com
        ATTENDEE;PARTSTAT=DECLINED;CN=Sergio:mailto:sergio@example.com
        END:VEVENT
        END:VCALENDAR
        </cal:calendar-data>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    /// <summary>Un home que solo tiene listas de tareas.</summary>
    public const string HomeWithoutEvents = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:cal="urn:ietf:params:xml:ns:caldav">
          <d:response>
            <d:href>/dav/calendars/user/sergio/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/dav/calendars/user/sergio/tareas/</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype><d:collection/><cal:calendar/></d:resourcetype>
                <cal:supported-calendar-component-set><cal:comp name="VTODO"/></cal:supported-calendar-component-set>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;
}
