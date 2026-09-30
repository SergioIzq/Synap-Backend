using Synap.Domain;

namespace Synap.UnitTests.Domain;

/// <summary>
/// specs/ai-assistant "Reminder moments resolved in the user's timezone" - now resolved by the
/// system rather than by the model (observable-failures design.md Decision 5).
/// </summary>
public class ReminderWordingTests
{
    private const string Madrid = "Europe/Madrid";

    /// <summary>Tuesday 29 September 2026, 19:50 in Madrid (17:50 UTC, summer time).</summary>
    private static readonly DateTime Now = new(2026, 9, 29, 17, 50, 0, DateTimeKind.Utc);

    private static DateTime? Resolve(string wording, string? timezone = Madrid)
        => ReminderWording.Resolve(wording, Now, timezone);

    /// <summary>
    /// The reminder this whole change started with: asked at 19:50 for "hoy a las 20:20" and
    /// never created, because the model would not compute the instant.
    /// </summary>
    [Fact]
    public void A_time_today_is_that_time_in_the_users_own_clock()
    {
        var resolved = Resolve("hoy a las 20:20");

        // 20:20 in Madrid is 18:20 UTC, not 20:20 UTC - two hours of difference that would have
        // delivered the reminder when the user had stopped waiting for it.
        Assert.Equal(new DateTime(2026, 9, 29, 18, 20, 0, DateTimeKind.Utc), resolved);
    }

    [Theory]
    [InlineData("a las 20:20")]
    [InlineData("20:20")]
    [InlineData("a las 20.20")]
    public void A_bare_time_still_today_means_today(string wording)
        => Assert.Equal(new DateTime(2026, 9, 29, 18, 20, 0, DateTimeKind.Utc), Resolve(wording));

    [Fact]
    public void A_bare_time_already_past_means_tomorrow()
        => Assert.Equal(new DateTime(2026, 9, 30, 6, 0, 0, DateTimeKind.Utc), Resolve("a las 8"));

    [Fact]
    public void A_day_without_a_time_is_nine_in_the_morning()
        => Assert.Equal(new DateTime(2026, 9, 30, 7, 0, 0, DateTimeKind.Utc), Resolve("manana"));

    [Fact]
    public void Tomorrow_evening_is_read_as_the_afternoon_hour()
        => Assert.Equal(new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc), Resolve("mañana a las 8 de la tarde"));

    [Fact]
    public void The_day_after_tomorrow_is_two_days_on()
        => Assert.Equal(new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc), Resolve("pasado mañana"));

    /// <summary>specs/ai-assistant "Relative day resolved": Tuesday's "el viernes" is this week's.</summary>
    [Fact]
    public void A_weekday_ahead_is_this_weeks()
        => Assert.Equal(new DateTime(2026, 10, 2, 7, 0, 0, DateTimeKind.Utc), Resolve("el viernes"));

    /// <summary>specs/ai-assistant "Day already passed this week": Tuesday's "el lunes" is next week's.</summary>
    [Fact]
    public void A_weekday_already_gone_is_next_weeks()
        => Assert.Equal(new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc), Resolve("el lunes"));

    [Fact]
    public void Today_as_a_weekday_with_an_hour_already_past_is_next_week()
        => Assert.Equal(new DateTime(2026, 10, 6, 7, 0, 0, DateTimeKind.Utc), Resolve("el martes a las 9"));

    [Fact]
    public void A_weekday_with_a_time_still_to_come_stays_today()
        => Assert.Equal(new DateTime(2026, 9, 29, 19, 0, 0, DateTimeKind.Utc), Resolve("el martes a las 21"));

    [Theory]
    [InlineData("en dos semanas", 2026, 10, 13, 17, 50)]
    [InlineData("en 3 dias", 2026, 10, 2, 17, 50)]
    [InlineData("en 45 minutos", 2026, 9, 29, 18, 35)]
    [InlineData("en una hora", 2026, 9, 29, 18, 50)]
    public void A_relative_offset_counts_from_now(string wording, int year, int month, int day, int hour, int minute)
        => Assert.Equal(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc), Resolve(wording));

    [Fact]
    public void A_named_date_is_that_date()
        => Assert.Equal(new DateTime(2026, 10, 15, 7, 0, 0, DateTimeKind.Utc), Resolve("el 15 de octubre"));

    [Fact]
    public void A_named_date_already_gone_this_year_is_next_year()
        => Assert.Equal(new DateTime(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc), Resolve("1 de marzo"));

    [Fact]
    public void An_iso_instant_is_taken_as_it_stands()
        => Assert.Equal(new DateTime(2026, 10, 3, 7, 0, 0, DateTimeKind.Utc), Resolve("2026-10-03T07:00:00Z"));

    /// <summary>specs/ai-assistant "No timezone supplied": resolved in UTC rather than refused.</summary>
    [Fact]
    public void Without_a_timezone_the_moment_is_resolved_in_utc()
        => Assert.Equal(new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc), Resolve("mañana", timezone: null));

    /// <summary>
    /// specs/ai-assistant "Moment that cannot be resolved": nothing is guessed. A reminder set for
    /// a moment nobody chose is worse than being asked to say when.
    /// </summary>
    [Theory]
    [InlineData("cuando pueda")]
    [InlineData("pronto")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("manana por si acaso")]
    [InlineData("el 31 de febrero")]
    [InlineData("a las 99")]
    [InlineData("en un rato")]
    public void Wording_that_names_no_moment_resolves_to_nothing(string wording)
        => Assert.Null(Resolve(wording));

    /// <summary>
    /// A moment explicitly named is resolved even when it has gone by: understanding it and
    /// accepting it are different questions, and Reminder.Create answers the second one with a
    /// message that says so rather than claiming not to have understood.
    /// </summary>
    [Fact]
    public void A_moment_explicitly_named_in_the_past_is_still_understood()
    {
        Assert.Equal(new DateTime(2026, 9, 29, 6, 0, 0, DateTimeKind.Utc), Resolve("hoy a las 8"));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Resolve("2020-01-01T00:00:00Z"));
    }

    /// <summary>
    /// The hour that does not exist on the spring switch: Madrid jumps 02:00 to 03:00 on
    /// 29 March 2026, and UserClock steps forward instead of throwing.
    /// </summary>
    [Fact]
    public void An_hour_that_does_not_exist_moves_forward_instead_of_failing()
    {
        var beforeTheSwitch = new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc);

        var resolved = ReminderWording.Resolve("mañana a las 2:30", beforeTheSwitch, Madrid);

        Assert.NotNull(resolved);
        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), resolved);
    }
}
