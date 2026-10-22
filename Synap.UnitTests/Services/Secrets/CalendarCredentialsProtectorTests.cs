using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Services.Secrets;
using System.Security.Cryptography;

namespace Synap.UnitTests.Services.Secrets;

/// <summary>
/// calendar-integration task 2.3 - credentials go in and out of the one encrypted blob the user
/// carries, and an unreadable blob degrades instead of throwing (design.md Decision 2).
/// </summary>
public class CalendarCredentialsProtectorTests
{
    private static CalendarCredentialsProtector WithKey(byte[]? key = null) =>
        new(new AesGcmSecretProtector(key ?? RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public void Google_credentials_round_trip()
    {
        var protector = WithKey();
        var stored = protector.Protect(new CalendarCredentials.Google("refresh-abc"));

        var read = protector.TryRead(CalendarProvider.Google, stored);

        Assert.Equal(new CalendarCredentials.Google("refresh-abc"), read);
    }

    [Fact]
    public void CalDav_credentials_round_trip()
    {
        var protector = WithKey();
        var original = new CalendarCredentials.CalDav("sergio", "secreto");
        var stored = protector.Protect(original);

        var read = protector.TryRead(CalendarProvider.CalDav, stored);

        Assert.Equal(original, read);
    }

    [Fact]
    public void The_stored_blob_does_not_contain_the_credentials()
    {
        var stored = WithKey().Protect(new CalendarCredentials.CalDav("sergio", "secreto"));

        Assert.StartsWith("v1:", stored);
        Assert.DoesNotContain("secreto", stored);
        Assert.DoesNotContain("cal.example.com", stored);
    }

    /// <summary>
    /// design.md Decision 2: losing or rotating SECRETS_ENCRYPTION_KEY disables calendars; it does
    /// not throw its way up through a briefing sweep.
    /// </summary>
    [Fact]
    public void A_blob_from_another_master_key_is_unreadable_rather_than_an_exception()
    {
        var stored = WithKey(RandomNumberGenerator.GetBytes(32)).Protect(new CalendarCredentials.Google("refresh-abc"));

        var read = WithKey(RandomNumberGenerator.GetBytes(32)).TryRead(CalendarProvider.Google, stored);

        Assert.Null(read);
    }

    [Fact]
    public void A_tampered_blob_is_unreadable()
    {
        var protector = WithKey();
        var stored = protector.Protect(new CalendarCredentials.Google("refresh-abc"));

        Assert.Null(protector.TryRead(CalendarProvider.Google, stored + "x"));
    }

    [Fact]
    public void Nothing_stored_reads_as_nothing()
    {
        Assert.Null(WithKey().TryRead(CalendarProvider.Google, null));
        Assert.Null(WithKey().TryRead(CalendarProvider.CalDav, "   "));
    }

    /// <summary>
    /// A blob whose shape does not match the kind recorded beside it - the pair got out of step
    /// somehow - is unreadable too, rather than a half-built credential.
    /// </summary>
    [Fact]
    public void A_blob_read_as_the_wrong_kind_is_unreadable()
    {
        var protector = WithKey();
        var stored = protector.Protect(new CalendarCredentials.Google("refresh-abc"));

        Assert.Null(protector.TryRead(CalendarProvider.CalDav, stored));
    }
}
