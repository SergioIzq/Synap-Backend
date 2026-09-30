using Synap.Domain;

namespace Synap.UnitTests.Domain;

/// <summary>daily-briefing task 1.1 - the setting and the "is one owed?" rule on the aggregate.</summary>
public class UserBriefingTests
{
    private const string Madrid = "Europe/Madrid";

    /// <summary>08:00 UTC is 10:00 in Madrid in September.</summary>
    private static readonly DateTime MorningUtc = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    private static User Enabled(int hour = 9, string? timezone = Madrid)
    {
        var user = UserGroqSettingsTests.NewUser();
        user.SetTimezone(timezone);
        Assert.True(user.SetBriefing(enabled: true, hour));
        return user;
    }

    /// <summary>specs/briefing "Off by default".</summary>
    [Fact]
    public void A_new_user_has_no_briefing()
    {
        var user = UserGroqSettingsTests.NewUser();

        Assert.False(user.BriefingEnabled);
        Assert.Null(user.BriefingHour);
        Assert.Null(user.BriefingLastResolvedOn);
        Assert.False(user.IsBriefingDue(MorningUtc));
    }

    [Fact]
    public void Turning_it_on_stores_the_hour()
    {
        var user = Enabled(hour: 7);

        Assert.True(user.BriefingEnabled);
        Assert.Equal(7, user.BriefingHour);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    [InlineData(99)]
    public void An_hour_outside_the_day_is_refused(int hour)
    {
        var user = UserGroqSettingsTests.NewUser();

        Assert.False(user.SetBriefing(enabled: true, hour));
        Assert.False(user.BriefingEnabled);
        Assert.Null(user.BriefingHour);
    }

    [Fact]
    public void Turning_it_on_without_ever_choosing_an_hour_is_refused()
    {
        var user = UserGroqSettingsTests.NewUser();

        Assert.False(user.SetBriefing(enabled: true, hour: null));
        Assert.False(user.BriefingEnabled);
    }

    [Fact]
    public void Turning_it_off_keeps_the_hour_for_next_time()
    {
        var user = Enabled(hour: 7);

        Assert.True(user.SetBriefing(enabled: false, hour: null));

        Assert.False(user.BriefingEnabled);
        Assert.Equal(7, user.BriefingHour);
        Assert.False(user.IsBriefingDue(MorningUtc));

        Assert.True(user.SetBriefing(enabled: true, hour: null));
        Assert.Equal(7, user.BriefingHour);
    }

    /// <summary>specs/briefing "Sent at the chosen hour" and "Before the chosen hour".</summary>
    [Fact]
    public void One_is_owed_once_the_local_hour_has_come()
    {
        Assert.True(Enabled(hour: 9).IsBriefingDue(MorningUtc));
        Assert.False(Enabled(hour: 11).IsBriefingDue(MorningUtc));
    }

    /// <summary>specs/briefing "Not sent twice in a day".</summary>
    [Fact]
    public void A_resolved_day_owes_nothing_more()
    {
        var user = Enabled();

        user.MarkBriefingResolved(new DateOnly(2026, 9, 30));

        Assert.False(user.IsBriefingDue(MorningUtc));
        // The next local day owes one again.
        Assert.True(user.IsBriefingDue(MorningUtc.AddDays(1)));
    }

    /// <summary>specs/briefing "The system was down at the hour" - hours late is still the same day.</summary>
    [Fact]
    public void An_hour_missed_earlier_today_is_still_owed()
    {
        var user = Enabled(hour: 7);

        Assert.True(user.IsBriefingDue(MorningUtc.AddHours(10)));
    }

    /// <summary>specs/briefing "The day is over" - yesterday's is never caught up.</summary>
    [Fact]
    public void Yesterdays_briefing_is_not_owed_today()
    {
        var user = Enabled(hour: 9);
        var today = UserClock.ToLocal(MorningUtc, Madrid);

        user.MarkBriefingResolved(DateOnly.FromDateTime(today));

        // Two days on, only the new day is owed - there is no backlog of one per missed day.
        Assert.True(user.IsBriefingDue(MorningUtc.AddDays(2)));
        Assert.Equal(new DateOnly(2026, 9, 30), user.BriefingLastResolvedOn);
    }

    /// <summary>specs/briefing "Each user on their own clock".</summary>
    [Fact]
    public void The_hour_is_the_users_own_not_utc()
    {
        var madrid = Enabled(hour: 9, timezone: Madrid);
        var mexico = Enabled(hour: 9, timezone: "America/Mexico_City");

        // 08:00 UTC: 10:00 in Madrid, 02:00 in Mexico City.
        Assert.True(madrid.IsBriefingDue(MorningUtc));
        Assert.False(mexico.IsBriefingDue(MorningUtc));
    }

    [Fact]
    public void Without_a_timezone_the_hour_is_utc()
    {
        var user = Enabled(hour: 9, timezone: null);

        // 08:00 UTC has not reached 09:00 anywhere; 09:00 UTC has, because UTC is the fallback.
        Assert.Null(user.Timezone);
        Assert.False(user.IsBriefingDue(MorningUtc));
        Assert.True(user.IsBriefingDue(MorningUtc.AddHours(1)));
    }
}
