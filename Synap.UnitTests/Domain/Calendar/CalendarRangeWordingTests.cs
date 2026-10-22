using Synap.Domain;

namespace Synap.UnitTests.Domain.Calendar;

/// <summary>
/// calendar-integration tarea 1.12: el intervalo en palabras se convierte en días locales completos
/// en `Europe/Madrid` (design.md Decisión 6).
/// </summary>
public class CalendarRangeWordingTests
{
    private const string Madrid = "Europe/Madrid";

    /// <summary>Jueves 22 de octubre de 2026, 10:00 en Madrid (CEST, UTC+2).</summary>
    private static readonly DateTime Thursday = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    private static CalendarRange Resolve(string wording, DateTime? nowUtc = null) =>
        CalendarRangeWording.Resolve(wording, nowUtc ?? Thursday, Madrid);

    private static DateTime Utc(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Today_is_the_whole_local_day()
    {
        var range = Resolve("hoy");

        Assert.Equal((Utc(10, 21, 22), Utc(10, 22, 22)), (range.FromUtc, range.ToUtc));
        Assert.True(range.Understood);
    }

    [Theory]
    [InlineData("mañana", 23)]
    [InlineData("Mañana", 23)]
    [InlineData("pasado mañana", 24)]
    [InlineData("el lunes", 26)]
    [InlineData("el jueves", 22)]
    [InlineData("el 30 de octubre", 30)]
    [InlineData("30/10", 30)]
    public void A_named_day_is_that_whole_day(string wording, int day)
    {
        var range = Resolve(wording);

        Assert.Equal(new DateOnly(2026, 10, day), range.FirstDay);
        Assert.Equal(1, range.Days);
        Assert.True(range.Understood);
    }

    [Fact]
    public void This_week_is_from_today_to_sunday()
    {
        var range = Resolve("esta semana");

        Assert.Equal(new DateOnly(2026, 10, 22), range.FirstDay);
        Assert.Equal(4, range.Days);
    }

    [Theory]
    [InlineData("la semana que viene")]
    [InlineData("la próxima semana")]
    public void Next_week_is_monday_to_sunday(string wording)
    {
        var range = Resolve(wording);

        Assert.Equal(new DateOnly(2026, 10, 26), range.FirstDay);
        Assert.Equal(7, range.Days);
    }

    [Fact]
    public void This_month_is_from_today_to_its_end()
    {
        var range = Resolve("este mes");

        Assert.Equal(new DateOnly(2026, 10, 22), range.FirstDay);
        Assert.Equal(10, range.Days);
    }

    [Theory]
    [InlineData("próximos 10 días", 10)]
    [InlineData("los próximos 3 días", 3)]
    [InlineData("próximos 400 días", CalendarRangeWording.MaxDays)]
    public void The_next_n_days_start_today(string wording, int days)
    {
        var range = Resolve(wording);

        Assert.Equal(new DateOnly(2026, 10, 22), range.FirstDay);
        Assert.Equal(days, range.Days);
    }

    [Theory]
    [InlineData("cuando sea")]
    [InlineData("")]
    [InlineData(null)]
    public void What_is_not_understood_is_the_next_thirty_days(string? wording)
    {
        var range = Resolve(wording!);

        Assert.False(range.Understood);
        Assert.Equal(new DateOnly(2026, 10, 22), range.FirstDay);
        Assert.Equal(CalendarRangeWording.DefaultDays, range.Days);
    }

    /// <summary>El 25 de octubre de 2026 tiene 25 horas en Madrid: cada medianoche lleva su propio desfase.</summary>
    [Fact]
    public void A_day_with_a_clock_change_keeps_its_local_midnights()
    {
        var saturday = new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);

        var range = Resolve("mañana", saturday);

        Assert.Equal(Utc(10, 24, 22), range.FromUtc); // 00:00 CEST
        Assert.Equal(Utc(10, 25, 23), range.ToUtc);   // 00:00 CET
        Assert.Equal(TimeSpan.FromHours(25), range.ToUtc - range.FromUtc);
    }

    [Fact]
    public void Late_in_the_evening_today_is_still_the_local_day()
    {
        var lateUtc = new DateTime(2026, 10, 22, 22, 30, 0, DateTimeKind.Utc); // 00:30 del viernes en Madrid

        var range = Resolve("hoy", lateUtc);

        Assert.Equal(new DateOnly(2026, 10, 23), range.FirstDay);
    }
}
