using Synap.Domain;
using Synap.UnitTests.Domain;

namespace Synap.UnitTests.Domain.Calendar;

/// <summary>calendar-integration tarea 1.1: la conexión y su marca de reconexión (design.md Decisión 2).</summary>
public class CalendarConnectionTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    private static CalendarConnection NewConnection(Guid? userId = null) => CalendarConnection.Create(
        userId ?? Guid.NewGuid(), CalendarProvider.CalDav, " sergio@fastmail.com ",
        "https://caldav.fastmail.com/dav/calendars/user/sergio/default/", "cifrado", Now);

    [Fact]
    public void Its_key_is_the_user()
    {
        var userId = Guid.NewGuid();

        var connection = NewConnection(userId);

        Assert.Equal(userId, connection.UserId.Value);
        Assert.Equal(userId, connection.Id.Value);
    }

    [Fact]
    public void A_new_connection_works_and_keeps_what_is_shown()
    {
        var connection = NewConnection();

        Assert.Equal(CalendarProvider.CalDav, connection.Provider);
        Assert.Equal("sergio@fastmail.com", connection.AccountLabel);
        Assert.Equal(Now, connection.ConnectedAt);
        Assert.False(connection.NeedsReconnect);
        Assert.Null(connection.NeedsReconnectSince);
    }

    [Theory]
    [InlineData("", "ref", "cifrado")]
    [InlineData("cuenta", " ", "cifrado")]
    [InlineData("cuenta", "ref", "")]
    public void It_is_not_created_without_account_reference_or_credentials(string account, string calendarRef, string credentials)
    {
        Assert.ThrowsAny<ArgumentException>(() => CalendarConnection.Create(
            Guid.NewGuid(), CalendarProvider.Google, account, calendarRef, credentials, Now));
    }

    [Fact]
    public void Marking_for_reconnect_keeps_the_first_moment()
    {
        var connection = NewConnection();

        connection.MarkNeedsReconnect(Now.AddHours(1));
        connection.MarkNeedsReconnect(Now.AddHours(5));

        Assert.True(connection.NeedsReconnect);
        Assert.Equal(Now.AddHours(1), connection.NeedsReconnectSince);
    }

    [Fact]
    public void Clearing_the_mark_leaves_it_working()
    {
        var connection = NewConnection();
        connection.MarkNeedsReconnect(Now);

        connection.ClearNeedsReconnect();

        Assert.False(connection.NeedsReconnect);
        Assert.Null(connection.NeedsReconnectSince);
    }

    [Fact]
    public void Replacing_credentials_touches_nothing_else()
    {
        var connection = NewConnection();
        connection.MarkNeedsReconnect(Now);

        connection.ReplaceCredentials("otro cifrado");

        Assert.Equal("otro cifrado", connection.CredentialsEncrypted);
        Assert.True(connection.NeedsReconnect);
        Assert.Equal(Now, connection.ConnectedAt);
    }
}

/// <summary>calendar-integration tarea 1.1: el evento y el nonce de OAuth del usuario.</summary>
public class CalendarEventTests
{
    [Fact]
    public void An_all_day_event_belongs_to_its_written_day()
    {
        var calendarEvent = CalendarEvent.AllDay("Puente", new DateOnly(2026, 11, 14));

        Assert.True(calendarEvent.IsAllDay);
        Assert.Equal(new DateOnly(2026, 11, 14), calendarEvent.AllDayDate);
        Assert.Equal(new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc), calendarEvent.StartUtc);
        Assert.Equal(new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc), calendarEvent.EndUtc);
    }

    [Fact]
    public void A_timed_event_is_not_all_day()
    {
        var start = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

        Assert.False(new CalendarEvent("Demo", start, start.AddHours(1)).IsAllDay);
    }

    [Fact]
    public void The_OAuth_nonce_is_single_use()
    {
        var user = UserGroqSettingsTests.NewUser();
        var now = DateTime.UtcNow;
        user.StartCalendarOAuth("nonce-1", now.AddMinutes(10));

        Assert.True(user.ConsumeCalendarOAuth("nonce-1", now));
        Assert.False(user.ConsumeCalendarOAuth("nonce-1", now));
    }

    [Fact]
    public void An_expired_or_foreign_nonce_fails_and_cancels_the_start()
    {
        var user = UserGroqSettingsTests.NewUser();
        var now = DateTime.UtcNow;

        user.StartCalendarOAuth("nonce-1", now.AddMinutes(10));
        Assert.False(user.ConsumeCalendarOAuth("otro", now));
        Assert.Null(user.CalendarOAuthNonce);

        user.StartCalendarOAuth("nonce-2", now.AddMinutes(10));
        Assert.False(user.ConsumeCalendarOAuth("nonce-2", now.AddMinutes(11)));
    }
}
