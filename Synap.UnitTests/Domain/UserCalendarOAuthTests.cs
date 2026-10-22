namespace Synap.UnitTests.Domain;

/// <summary>calendar-integration tarea 1.2: el nonce de un solo uso del consentimiento de Google.</summary>
public class UserCalendarOAuthTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void El_nonce_vigente_se_acepta_una_sola_vez()
    {
        var user = UserGroqSettingsTests.NewUser();
        user.StartCalendarOAuth("n1", Now.AddMinutes(10));

        Assert.True(user.ConsumeCalendarOAuth("n1", Now.AddMinutes(5)));
        Assert.False(user.ConsumeCalendarOAuth("n1", Now.AddMinutes(5)));
    }

    [Fact]
    public void Un_nonce_caducado_no_se_acepta()
    {
        var user = UserGroqSettingsTests.NewUser();
        user.StartCalendarOAuth("n1", Now.AddMinutes(10));

        Assert.False(user.ConsumeCalendarOAuth("n1", Now.AddMinutes(11)));
    }

    [Fact]
    public void Un_nonce_que_no_coincide_invalida_el_vigente()
    {
        var user = UserGroqSettingsTests.NewUser();
        user.StartCalendarOAuth("n1", Now.AddMinutes(10));

        Assert.False(user.ConsumeCalendarOAuth("otro", Now));
        Assert.False(user.ConsumeCalendarOAuth("n1", Now));
    }

    [Fact]
    public void Un_inicio_nuevo_sustituye_al_anterior()
    {
        var user = UserGroqSettingsTests.NewUser();
        user.StartCalendarOAuth("n1", Now.AddMinutes(10));
        user.StartCalendarOAuth("n2", Now.AddMinutes(10));

        Assert.False(user.ConsumeCalendarOAuth("n1", Now));
    }
}
