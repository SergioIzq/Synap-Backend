using Synap.Domain;

namespace Synap.UnitTests.Domain;

/// <summary>calendar-integration tarea 1.14: la antelación respecto a un evento (design.md Decisión 7).</summary>
public class ReminderOffsetWordingTests
{
    private const string Madrid = "Europe/Madrid";

    /// <summary>Viernes 30 de octubre de 2026, 12:00 en Madrid (CET, UTC+1).</summary>
    private static readonly CalendarEvent Demo =
        new("Demo", new DateTime(2026, 10, 30, 11, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc));

    private static DateTime? Resolve(string wording, CalendarEvent? calendarEvent = null)
        => ReminderOffsetWording.Resolve(wording, calendarEvent ?? Demo, Madrid);

    [Theory]
    [InlineData("una hora antes", 10, 0)]
    [InlineData("1 hora antes", 10, 0)]
    [InlineData("dos horas antes", 9, 0)]
    [InlineData("media hora antes", 10, 30)]
    [InlineData("30 minutos antes", 10, 30)]
    [InlineData("15 minutos antes", 10, 45)]
    public void Minutes_and_hours_are_taken_from_the_start(string wording, int hourUtc, int minuteUtc)
        => Assert.Equal(new DateTime(2026, 10, 30, hourUtc, minuteUtc, 0, DateTimeKind.Utc), Resolve(wording));

    /// <summary>Un día sin hora son las 09:00 locales: el jueves 29 a las 09:00 CET son las 08:00 UTC.</summary>
    [Theory]
    [InlineData("el día antes", 29)]
    [InlineData("un día antes", 29)]
    [InlineData("dos días antes", 28)]
    public void Days_before_are_at_nine_local(string wording, int day)
        => Assert.Equal(new DateTime(2026, 10, day, 8, 0, 0, DateTimeKind.Utc), Resolve(wording));

    /// <summary>Con cambio de hora por medio: el sábado 24 a las 09:00 CEST son las 07:00 UTC.</summary>
    [Fact]
    public void Days_before_across_a_clock_change_keep_nine_local()
    {
        var monday = new CalendarEvent("Revisión", new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 10, 24, 7, 0, 0, DateTimeKind.Utc), Resolve("dos días antes", monday));
    }

    [Fact]
    public void An_all_day_event_counts_from_its_local_midnight()
    {
        var holiday = CalendarEvent.AllDay("Puente", new DateOnly(2026, 11, 2));

        Assert.Equal(new DateTime(2026, 11, 1, 22, 0, 0, DateTimeKind.Utc), Resolve("una hora antes", holiday));
        Assert.Equal(new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), Resolve("el día antes", holiday));
    }

    [Theory]
    [InlineData("")]
    [InlineData("cuando quieras")]
    [InlineData("una hora después")]
    [InlineData("0 minutos antes")]
    public void What_is_not_understood_is_null(string wording) => Assert.Null(Resolve(wording));
}
