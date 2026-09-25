using Synap.Domain;
using Synap.Shared.Domain.ValueObjects;

namespace Synap.UnitTests.Domain;

/// <summary>password-recovery task 1.1.</summary>
public class UserPasswordResetTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void New_user_has_a_security_stamp_and_no_pending_reset()
    {
        var user = UserGroqSettingsTests.NewUser();

        Assert.Equal(32, user.SecurityStamp.Length);
        Assert.False(user.HasValidPasswordReset(Now));
    }

    [Fact]
    public void Reset_is_valid_until_it_expires()
    {
        var user = UserGroqSettingsTests.NewUser();

        user.StartPasswordReset("hash", Now.AddHours(1));

        Assert.True(user.HasValidPasswordReset(Now.AddMinutes(59)));
        Assert.False(user.HasValidPasswordReset(Now.AddHours(1).AddSeconds(1)));
    }

    [Fact]
    public void A_newer_request_replaces_the_previous_token()
    {
        var user = UserGroqSettingsTests.NewUser();

        user.StartPasswordReset("first", Now.AddHours(1));
        user.StartPasswordReset("second", Now.AddHours(1));

        Assert.Equal("second", user.PasswordResetTokenHash);
    }

    [Fact]
    public void Completing_a_reset_clears_the_token_changes_the_password_and_rotates_the_stamp()
    {
        var user = UserGroqSettingsTests.NewUser();
        var stamp = user.SecurityStamp;
        user.StartPasswordReset("hash", Now.AddHours(1));

        user.CompletePasswordReset(PasswordHash.CreateFromDatabase("new-hash"));

        Assert.Null(user.PasswordResetTokenHash);
        Assert.Null(user.PasswordResetExpiresAt);
        Assert.Equal("new-hash", user.PasswordHash.Value);
        Assert.NotEqual(stamp, user.SecurityStamp);
    }

    [Fact]
    public void Changing_the_password_rotates_the_stamp()
    {
        var user = UserGroqSettingsTests.NewUser();
        var stamp = user.SecurityStamp;

        user.ChangePassword(PasswordHash.CreateFromDatabase("other"));

        Assert.NotEqual(stamp, user.SecurityStamp);
    }
}
