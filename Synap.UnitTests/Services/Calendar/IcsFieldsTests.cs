using Synap.Infrastructure.Services.Calendar;

namespace Synap.UnitTests.Services.Calendar;

/// <summary>calendar-store-and-view tarea 1.2: las columnas derivadas de un `.ics` (design.md Decisión 1).</summary>
public class IcsFieldsTests
{
    private static string Ics(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n"
        + string.Concat(events.Select(e => $"BEGIN:VEVENT\r\n{e.Replace("\n", "\r\n")}\r\nEND:VEVENT\r\n"))
        + "END:VCALENDAR\r\n";

    private const string Madrid = """
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
        """;

    [Fact]
    public void A_single_event_has_its_start_and_end()
    {
        var fields = IcsFields.TryRead(Ics("""
            UID:cena
            DTSTAMP:20261001T120000Z
            DTSTART:20261114T190000Z
            DTEND:20261114T210000Z
            SUMMARY: Cena con Marta
            """));

        Assert.NotNull(fields);
        Assert.Equal("cena", fields.Uid);
        Assert.Equal("Cena con Marta", fields.Summary);
        Assert.Equal(new DateTime(2026, 11, 14, 19, 0, 0, DateTimeKind.Utc), fields.StartsAtUtc);
        Assert.Equal(new DateTime(2026, 11, 14, 21, 0, 0, DateTimeKind.Utc), fields.EndsAtUtc);
        Assert.False(fields.IsRecurring);
        Assert.False(fields.AllDay);
    }

    [Fact]
    public void A_zoned_event_is_converted_to_utc()
    {
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + Madrid.Replace("\n", "\r\n") + "\r\n"
            + "BEGIN:VEVENT\r\nUID:z\r\nDTSTAMP:20261001T120000Z\r\nDTSTART;TZID=Europe/Madrid:20261020T090000\r\n"
            + "DURATION:PT30M\r\nSUMMARY:Daily\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var fields = IcsFields.TryRead(ics);

        Assert.Equal(new DateTime(2026, 10, 20, 7, 0, 0, DateTimeKind.Utc), fields!.StartsAtUtc);
        Assert.Equal(new DateTime(2026, 10, 20, 7, 30, 0, DateTimeKind.Utc), fields.EndsAtUtc);
    }

    [Fact]
    public void An_all_day_event_keeps_its_day()
    {
        var fields = IcsFields.TryRead(Ics("""
            UID:toledo
            DTSTAMP:20261001T120000Z
            DTSTART;VALUE=DATE:20261114
            DTEND;VALUE=DATE:20261116
            SUMMARY:Escapada a Toledo
            """));

        Assert.True(fields!.AllDay);
        Assert.Equal(new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc), fields.StartsAtUtc);
        Assert.Equal(new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), fields.EndsAtUtc);
    }

    [Fact]
    public void An_all_day_event_without_end_lasts_one_day()
    {
        var fields = IcsFields.TryRead(Ics("""
            UID:cumple
            DTSTAMP:20261001T120000Z
            DTSTART;VALUE=DATE:20261114
            SUMMARY:Cumpleaños
            """));

        Assert.Equal(new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc), fields!.EndsAtUtc);
    }

    [Fact]
    public void A_rule_with_until_ends_with_its_last_occurrence()
    {
        var fields = IcsFields.TryRead(Ics("""
            UID:curso
            DTSTAMP:20261001T120000Z
            DTSTART:20261005T170000Z
            DTEND:20261005T180000Z
            RRULE:FREQ=WEEKLY;UNTIL=20261026T170000Z
            SUMMARY:Curso
            """));

        Assert.True(fields!.IsRecurring);
        Assert.Equal(new DateTime(2026, 10, 5, 17, 0, 0, DateTimeKind.Utc), fields.StartsAtUtc);
        Assert.Equal(new DateTime(2026, 10, 26, 18, 0, 0, DateTimeKind.Utc), fields.EndsAtUtc);
    }

    [Fact]
    public void A_rule_with_count_ends_with_its_last_occurrence()
    {
        var fields = IcsFields.TryRead(Ics("""
            UID:fisio
            DTSTAMP:20261001T120000Z
            DTSTART:20261001T080000Z
            DTEND:20261001T090000Z
            RRULE:FREQ=DAILY;COUNT=3
            SUMMARY:Fisio
            """));

        Assert.True(fields!.IsRecurring);
        Assert.Equal(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc), fields.EndsAtUtc);
    }

    [Fact]
    public void A_rule_without_end_has_no_end()
    {
        var fields = IcsFields.TryRead(Ics("""
            UID:sprint
            DTSTAMP:20261001T120000Z
            DTSTART:20260907T070000Z
            DTEND:20260907T073000Z
            RRULE:FREQ=WEEKLY;BYDAY=MO
            SUMMARY:Reunión de sprint
            """));

        Assert.True(fields!.IsRecurring);
        Assert.Null(fields.EndsAtUtc);
    }

    /// <summary>La excepción no es el maestro y, si se mueve más allá de la regla, alarga el fin.</summary>
    [Fact]
    public void A_master_with_a_moved_exception_takes_the_master_and_the_later_end()
    {
        var fields = IcsFields.TryRead(Ics(
            """
            UID:retro
            DTSTAMP:20261001T120000Z
            DTSTART:20261005T150000Z
            DTEND:20261005T160000Z
            RRULE:FREQ=WEEKLY;COUNT=2
            SUMMARY:Retro
            """,
            """
            UID:retro
            DTSTAMP:20261001T120000Z
            RECURRENCE-ID:20261012T150000Z
            DTSTART:20261014T150000Z
            DTEND:20261014T160000Z
            SUMMARY:Retro (movida)
            """));

        Assert.Equal("Retro", fields!.Summary);
        Assert.Equal(new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc), fields.StartsAtUtc);
        Assert.Equal(new DateTime(2026, 10, 14, 16, 0, 0, DateTimeKind.Utc), fields.EndsAtUtc);
        Assert.True(fields.IsRecurring);
    }

    [Theory]
    [InlineData("esto no es un ics")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nUID:t\r\nSUMMARY:Tarea\r\nEND:VTODO\r\nEND:VCALENDAR\r\n")]
    public void Something_without_a_readable_event_gives_nothing(string ics)
    {
        Assert.Null(IcsFields.TryRead(ics));
    }
}
