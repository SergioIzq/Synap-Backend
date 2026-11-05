using Synap.Infrastructure.Services.Calendar;
using System.Xml.Linq;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>
/// calendar-store-and-view tarea 1.6: las ocurrencias de un `.ics` guardado, sobre los `.ics`
/// grabados de iCloud, Fastmail y Nextcloud (specs/calendar-integration "Qué se lee de un evento").
/// </summary>
public class IcsOccurrencesTests
{
    private const string Madrid = "Europe/Madrid";
    private const string MexicoCity = "America/Mexico_City";

    /// <summary>El `.ics` de cada respuesta de una grabación, como se guarda en la copia.</summary>
    private static IReadOnlyList<string> IcsOf(string multiStatus) =>
        XDocument.Parse(multiStatus)
            .Descendants(XName.Get("calendar-data", "urn:ietf:params:xml:ns:caldav"))
            .Select(d => d.Value)
            .ToList();

    private static List<Synap.Domain.CalendarEvent> Expand(
        string multiStatus, DateTime fromUtc, DateTime toUtc, string? timezone = Madrid, string? username = "sergio@example.com") =>
        IcsOf(multiStatus).SelectMany(ics => IcsOccurrences.Expand(ics, username, timezone, fromUtc, toUtc)).ToList();

    private static string Ics(string body) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\n" + body.Replace("\n", "\r\n") + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    /// <summary>`TZID` con VTIMEZONE embebido: 18:00 en Ciudad de México son las 00:00 UTC del día siguiente.</summary>
    [Fact]
    public void ICloud_times_with_tzid_come_back_in_utc_with_their_details()
    {
        var events = Expand(CalDavRecordings.ICloudReport,
            new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc));

        var dinner = Assert.Single(events);
        Assert.Equal("Cena con Marta", dinner.Title);
        Assert.Equal(new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc), dinner.StartUtc);
        Assert.Equal(new DateTime(2026, 11, 14, 1, 0, 0, DateTimeKind.Utc), dinner.EndUtc);
        Assert.False(dinner.IsAllDay);
        Assert.False(dinner.IsRecurring);
        Assert.Equal("Roma Norte", dinner.Location);
        Assert.Equal("Llevar el libro, y el regalo", dinner.Description);
    }

    /// <summary>`VALUE=DATE` y `STATUS:CANCELLED`.</summary>
    [Fact]
    public void Fastmail_all_day_events_keep_their_days_and_cancelled_ones_are_left_out()
    {
        var events = Expand(CalDavRecordings.FastmailReport,
            new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));

        var trip = Assert.Single(events);
        Assert.Equal("Escapada a Toledo", trip.Title);
        Assert.Equal(new DateOnly(2026, 11, 14), trip.AllDayDate);
        Assert.Equal(new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), trip.EndUtc);
    }

    /// <summary>
    /// specs "Evento recurrente" y "Ocurrencia movida": la regla se expande con la excepción movida
    /// y el cambio de hora del 25 de octubre; la invitación rechazada (`DECLINED`) no se devuelve
    /// (specs "Evento rechazado o cancelado").
    /// </summary>
    [Fact]
    public void Rules_are_expanded_with_their_moved_occurrence_and_declined_invitations_left_out()
    {
        var events = Expand(CalDavRecordings.NextcloudReportNotExpanded,
            new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.All(events, e => Assert.Equal("Reunión de sprint", e.Title));
        Assert.All(events, e => Assert.True(e.IsRecurring));
        Assert.Equal(
            [
                new DateTime(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc), // lunes 09:00 CEST
                new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc), // movida a las 10:00 CEST
                new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc), // 09:00 CET, tras el cambio de hora
            ],
            events.Select(e => e.StartUtc));
    }

    /// <summary>specs "Ocurrencia movida": movida del lunes al martes, esa semana sale el martes y no el lunes.</summary>
    [Fact]
    public void An_occurrence_moved_to_another_day_is_returned_on_that_day_only()
    {
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n"
            + "BEGIN:VEVENT\r\nUID:w\r\nDTSTAMP:20261001T000000Z\r\nDTSTART:20261005T080000Z\r\nDTEND:20261005T090000Z\r\n"
            + "RRULE:FREQ=WEEKLY\r\nSUMMARY:Semanal\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:w\r\nDTSTAMP:20261001T000000Z\r\nRECURRENCE-ID:20261012T080000Z\r\n"
            + "DTSTART:20261013T080000Z\r\nDTEND:20261013T090000Z\r\nSUMMARY:Semanal\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var week = IcsOccurrences.Expand(ics, null, Madrid,
            new DateTime(2026, 10, 11, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 18, 22, 0, 0, DateTimeKind.Utc)).ToList();

        Assert.Equal([new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc)], week.Select(e => e.StartUtc));
    }

    /// <summary>specs "Evento de todo el día": el 14 de noviembre en Ciudad de México es el 14, ni el 13 ni el 15.</summary>
    [Fact]
    public void An_all_day_event_belongs_to_its_day_in_mexico_city()
    {
        var ics = Ics("""
            UID:feria
            DTSTAMP:20261001T120000Z
            DTSTART;VALUE=DATE:20261114
            DTEND;VALUE=DATE:20261115
            SUMMARY:Feria
            """);

        static (DateTime From, DateTime To) DayInMexico(int day) =>
            // Noviembre: CST, UTC-06:00.
            (new DateTime(2026, 11, day, 6, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, day + 1, 6, 0, 0, DateTimeKind.Utc));

        var (from13, to13) = DayInMexico(13);
        var (from14, to14) = DayInMexico(14);
        var (from15, to15) = DayInMexico(15);

        Assert.Empty(IcsOccurrences.Expand(ics, null, MexicoCity, from13, to13));
        Assert.Equal(new DateOnly(2026, 11, 14), Assert.Single(IcsOccurrences.Expand(ics, null, MexicoCity, from14, to14)).AllDayDate);
        Assert.Empty(IcsOccurrences.Expand(ics, null, MexicoCity, from15, to15));
    }

    /// <summary>Una hora flotante se lee en la zona del usuario.</summary>
    [Fact]
    public void A_floating_time_is_read_in_the_users_timezone()
    {
        var ics = Ics("""
            UID:flot
            DTSTAMP:20261001T120000Z
            DTSTART:20261114T100000
            DTEND:20261114T110000
            SUMMARY:Médico
            """);

        var visit = Assert.Single(IcsOccurrences.Expand(ics, null, MexicoCity,
            new DateTime(2026, 11, 14, 6, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 15, 6, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(new DateTime(2026, 11, 14, 16, 0, 0, DateTimeKind.Utc), visit.StartUtc);
    }

    /// <summary>Un evento que empezó antes del intervalo y sigue en él se devuelve.</summary>
    [Fact]
    public void An_event_already_under_way_is_returned()
    {
        var ics = Ics("""
            UID:congreso
            DTSTAMP:20261001T120000Z
            DTSTART:20261113T080000Z
            DTEND:20261115T180000Z
            SUMMARY:Congreso
            """);

        Assert.Single(IcsOccurrences.Expand(ics, null, Madrid,
            new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Without_the_users_address_a_declined_invitation_is_shown()
    {
        var events = Expand(CalDavRecordings.NextcloudReportNotExpanded,
            new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc),
            username: "otra@example.com");

        Assert.Contains(events, e => e.Title == "Comida de equipo");
    }
}
