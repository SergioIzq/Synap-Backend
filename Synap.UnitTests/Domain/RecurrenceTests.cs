using Synap.Domain;

namespace Synap.UnitTests.Domain;

/// <summary>assistant-reminders task 1.2.</summary>
public class RecurrenceTests
{
    private const string Madrid = "Europe/Madrid";

    [Theory]
    [InlineData("daily", RecurrenceKind.Daily, 0)]
    [InlineData("DAILY", RecurrenceKind.Daily, 0)]
    [InlineData(" daily ", RecurrenceKind.Daily, 0)]
    [InlineData("weekly:0", RecurrenceKind.Weekly, 0)]
    [InlineData("weekly:6", RecurrenceKind.Weekly, 6)]
    [InlineData("monthly:1", RecurrenceKind.Monthly, 1)]
    [InlineData("monthly:28", RecurrenceKind.Monthly, 28)]
    public void Each_supported_form_is_parsed(string raw, RecurrenceKind kind, int value)
    {
        var parsed = Recurrence.Parse(raw);

        Assert.True(parsed.IsSuccess);
        Assert.Equal(kind, parsed.Value.Kind);
        Assert.Equal(value, parsed.Value.Value);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("weekly:3")]
    [InlineData("monthly:15")]
    public void A_parsed_recurrence_renders_back_to_its_stored_form(string raw)
        => Assert.Equal(raw, Recurrence.Parse(raw).Value.ToString());

    [Theory]
    [InlineData("weekly:7")]
    [InlineData("weekly:-1")]
    [InlineData("monthly:0")]
    [InlineData("monthly:29")]
    [InlineData("monthly:31")]
    [InlineData("weekly")]
    [InlineData("weekly:")]
    [InlineData("weekly:lunes")]
    [InlineData("every weekday")]
    [InlineData("el tercer martes del mes")]
    [InlineData("yearly:1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_else_is_rejected(string? raw)
    {
        var parsed = Recurrence.Parse(raw);

        Assert.True(parsed.IsFailure);
        Assert.Equal(Recurrence.Invalid, parsed.Error);
    }

    [Fact]
    public void A_missing_recurrence_is_a_one_off_reminder_not_a_failure()
    {
        var parsed = Recurrence.ParseOptional(null);

        Assert.True(parsed.IsSuccess);
        Assert.Null(parsed.Value);
    }

    [Fact]
    public void An_invalid_recurrence_is_still_a_failure_when_optional()
        => Assert.True(Recurrence.ParseOptional("weekly:9").IsFailure);

    [Fact]
    public void Daily_advances_one_day_keeping_the_time_of_day()
    {
        // 09:00 in Madrid is 07:00 UTC in summer.
        var next = Recurrence.Daily.NextAfter(new DateTime(2026, 7, 10, 7, 0, 0, DateTimeKind.Utc), Madrid);

        Assert.Equal(new DateTime(2026, 7, 11, 7, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Weekly_advances_to_the_next_matching_weekday()
    {
        // Monday 2026-07-06 18:00 Madrid (16:00 UTC), repeating on Mondays.
        var monday = Recurrence.Weekly(0).Value;

        var next = monday.NextAfter(new DateTime(2026, 7, 6, 16, 0, 0, DateTimeKind.Utc), Madrid);

        Assert.Equal(new DateTime(2026, 7, 13, 16, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Weekly_from_another_weekday_lands_on_the_next_one()
    {
        // From Monday, a Thursday reminder is three days away.
        var thursday = Recurrence.Weekly(3).Value;

        var next = thursday.NextAfter(new DateTime(2026, 7, 6, 16, 0, 0, DateTimeKind.Utc), Madrid);

        Assert.Equal(new DateTime(2026, 7, 9, 16, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Monthly_advances_to_the_same_day_of_the_following_month()
    {
        var fifteenth = Recurrence.Monthly(15).Value;

        var next = fifteenth.NextAfter(new DateTime(2026, 7, 15, 7, 0, 0, DateTimeKind.Utc), Madrid);

        Assert.Equal(new DateTime(2026, 8, 15, 7, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Monthly_rolls_over_a_short_month()
    {
        // 28 January -> 28 February, the reason days 29-31 are not allowed.
        var twentyEighth = Recurrence.Monthly(28).Value;

        var next = twentyEighth.NextAfter(new DateTime(2026, 1, 28, 8, 0, 0, DateTimeKind.Utc), Madrid);

        Assert.Equal(new DateTime(2026, 2, 28, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Monthly_rolls_over_the_end_of_the_year()
    {
        var fifth = Recurrence.Monthly(5).Value;

        var next = fifth.NextAfter(new DateTime(2026, 12, 5, 8, 0, 0, DateTimeKind.Utc), Madrid);

        Assert.Equal(new DateTime(2027, 1, 5, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void A_daily_reminder_keeps_its_local_time_across_the_spring_dst_switch()
    {
        // Madrid goes CET (+1) -> CEST (+2) on 2026-03-29. 09:00 local is 08:00 UTC before and
        // 07:00 UTC after: the instant moves so the wall clock does not.
        var next = Recurrence.Daily.NextAfter(new DateTime(2026, 3, 28, 8, 0, 0, DateTimeKind.Utc), Madrid);

        Assert.Equal(new DateTime(2026, 3, 29, 7, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void A_daily_reminder_keeps_its_local_time_across_the_autumn_dst_switch()
    {
        // Madrid goes CEST (+2) -> CET (+1) on 2026-10-25.
        var next = Recurrence.Daily.NextAfter(new DateTime(2026, 10, 24, 7, 0, 0, DateTimeKind.Utc), Madrid);

        Assert.Equal(new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void A_local_time_that_the_spring_switch_skips_moves_forward_instead_of_throwing()
    {
        // 02:30 Madrid does not exist on 2026-03-29: the clocks jump 02:00 -> 03:00.
        var next = Recurrence.Daily.NextAfter(new DateTime(2026, 3, 28, 1, 30, 0, DateTimeKind.Utc), Madrid);

        // 03:00 CEST is 01:00 UTC.
        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void An_empty_timezone_resolves_in_utc()
    {
        var next = Recurrence.Daily.NextAfter(new DateTime(2026, 7, 10, 7, 0, 0, DateTimeKind.Utc), null);

        Assert.Equal(new DateTime(2026, 7, 11, 7, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void An_unknown_timezone_resolves_in_utc_rather_than_failing_delivery()
    {
        var next = Recurrence.Daily.NextAfter(new DateTime(2026, 7, 10, 7, 0, 0, DateTimeKind.Utc), "Mars/Olympus_Mons");

        Assert.Equal(new DateTime(2026, 7, 11, 7, 0, 0, DateTimeKind.Utc), next);
    }
}
