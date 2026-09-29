using Synap.Domain;

namespace Synap.UnitTests.Domain;

/// <summary>assistant-reminders task 1.6.</summary>
public class UserTelegramTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_new_user_has_no_telegram_and_no_timezone()
    {
        var user = UserGroqSettingsTests.NewUser();

        Assert.False(user.HasTelegram);
        Assert.Null(user.Timezone);
        Assert.False(user.HasValidTelegramLink(Now));
    }

    [Fact]
    public void A_link_code_is_valid_until_it_expires()
    {
        var user = UserGroqSettingsTests.NewUser();

        user.StartTelegramLink("token", Now.AddMinutes(15));

        Assert.True(user.HasValidTelegramLink(Now.AddMinutes(14)));
        Assert.False(user.HasValidTelegramLink(Now.AddMinutes(15).AddSeconds(1)));
    }

    [Fact]
    public void A_newer_request_replaces_the_previous_code()
    {
        var user = UserGroqSettingsTests.NewUser();

        user.StartTelegramLink("first", Now.AddMinutes(15));
        user.StartTelegramLink("second", Now.AddMinutes(15));

        Assert.Equal("second", user.TelegramLinkToken);
    }

    [Fact]
    public void Completing_the_link_stores_the_chat_and_clears_the_code()
    {
        var user = UserGroqSettingsTests.NewUser();
        user.StartTelegramLink("token", Now.AddMinutes(15));

        user.CompleteTelegramLink("123456789");

        Assert.True(user.HasTelegram);
        Assert.Equal("123456789", user.TelegramChatId);
        Assert.Null(user.TelegramLinkToken);
        Assert.Null(user.TelegramLinkTokenExpiresAt);
        Assert.False(user.HasValidTelegramLink(Now));
    }

    [Fact]
    public void Disconnecting_clears_the_chat_and_any_outstanding_code()
    {
        var user = UserGroqSettingsTests.NewUser();
        user.StartTelegramLink("token", Now.AddMinutes(15));
        user.CompleteTelegramLink("123456789");
        user.StartTelegramLink("another", Now.AddMinutes(15));

        user.DisconnectTelegram();

        Assert.False(user.HasTelegram);
        Assert.Null(user.TelegramChatId);
        Assert.Null(user.TelegramLinkToken);
    }

    [Theory]
    [InlineData("Europe/Madrid")]
    [InlineData("America/New_York")]
    [InlineData("UTC")]
    [InlineData("  Europe/Madrid  ")]
    public void A_known_timezone_is_stored_trimmed(string sent)
    {
        var user = UserGroqSettingsTests.NewUser();

        Assert.True(user.SetTimezone(sent));
        Assert.Equal(sent.Trim(), user.Timezone);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("not a timezone")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_unknown_timezone_is_ignored(string? sent)
    {
        var user = UserGroqSettingsTests.NewUser();
        user.SetTimezone("Europe/Madrid");

        Assert.False(user.SetTimezone(sent));
        Assert.Equal("Europe/Madrid", user.Timezone);
    }
}
