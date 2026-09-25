using Dapper;
using Synap.Api;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>password-recovery tasks 4.1-4.4 - specs/identity "Password recovery by email".</summary>
[Collection(ApiCollection.Name)]
public class PasswordRecoveryApiTests
{
    private const string NewPassword = "Recuperada-2026";
    private const string InvalidLink = "El enlace no es válido o ha caducado. Solicita uno nuevo.";

    private readonly ApiFixture _api;

    public PasswordRecoveryApiTests(ApiFixture api) => _api = api;

    private Task<HttpResponseMessage> ForgotAsync(string email, HttpClient? client = null)
        => (client ?? _api.CreateClient()).PostAsJsonAsync("/api/auth/forgot-password", new { email });

    private Task<HttpResponseMessage> ResetAsync(string token, string newPassword = NewPassword)
        => _api.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword });

    [Fact]
    public async Task Known_and_unknown_emails_get_the_same_response_and_only_the_known_one_gets_mail()
    {
        var (email, _) = await _api.CreateClient().RegisterAndLoginAsync();
        var unknown = $"{Guid.NewGuid():N}@example.com";

        var known = await ForgotAsync(email);
        var missing = await ForgotAsync(unknown);

        Assert.True(known.IsSuccessStatusCode);
        Assert.Equal(known.StatusCode, missing.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());

        var mail = Assert.Single(_api.Emails.To(email));
        Assert.Equal("Restablece tu contraseña de Synap", mail.Subject);
        Assert.Contains("http://localhost:4200/auth/reset-password?token=", mail.TextBody);
        Assert.DoesNotContain(email, mail.TextBody.Split('\n').Single(l => l.Contains("reset-password")));
        Assert.Empty(_api.Emails.To(unknown));
    }

    [Fact]
    public async Task Only_the_token_hash_is_stored()
    {
        var (email, _) = await _api.CreateClient().RegisterAndLoginAsync();
        await ForgotAsync(email);
        var token = _api.Emails.LatestResetToken(email);

        await using var connection = await _api.OpenConnectionAsync();
        var stored = await connection.ExecuteScalarAsync<string>("SELECT password_reset_token_hash FROM users WHERE email = @email", new { email });

        Assert.NotNull(stored);
        Assert.Equal(64, stored!.Length);
        Assert.NotEqual(token, stored);
    }

    [Fact]
    public async Task Valid_link_resets_once_ends_sessions_and_only_the_new_password_works()
    {
        var client = _api.CreateClient();
        var (email, oldSession) = await client.RegisterAndLoginAsync();
        await ForgotAsync(email);
        var token = _api.Emails.LatestResetToken(email);

        (await ResetAsync(token)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.CreateClient().WithBearer(oldSession).GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = ApiClientExtensions.Password })).StatusCode);
        Assert.NotEmpty(await _api.CreateClient().LoginAsync(email, NewPassword));

        var reused = await ResetAsync(token, "OtraMas-2026");
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal(InvalidLink, await reused.ErrorMessageAsync());
    }

    [Fact]
    public async Task Invalid_new_password_is_rejected_and_the_link_stays_usable()
    {
        var (email, _) = await _api.CreateClient().RegisterAndLoginAsync();
        await ForgotAsync(email);
        var token = _api.Emails.LatestResetToken(email);

        var weak = await ResetAsync(token, "corta");

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("entre 8 y 128", await weak.ErrorMessageAsync());
        (await ResetAsync(token)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Expired_link_is_rejected_with_the_same_message_and_cleared()
    {
        var (email, _) = await _api.CreateClient().RegisterAndLoginAsync();
        await ForgotAsync(email);
        var token = _api.Emails.LatestResetToken(email);

        await using (var connection = await _api.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET password_reset_expires_at = now() - interval '1 minute' WHERE email = @email", new { email });
        }

        var expired = await ResetAsync(token);

        Assert.Equal(InvalidLink, await expired.ErrorMessageAsync());
        await using var check = await _api.OpenConnectionAsync();
        Assert.Null(await check.ExecuteScalarAsync<string>("SELECT password_reset_token_hash FROM users WHERE email = @email", new { email }));
    }

    [Fact]
    public async Task Unknown_link_gets_the_same_message()
    {
        var response = await ResetAsync("no-es-un-token-real");

        Assert.Equal(InvalidLink, await response.ErrorMessageAsync());
    }

    [Fact]
    public async Task A_newer_request_invalidates_the_older_link()
    {
        var (email, _) = await _api.CreateClient().RegisterAndLoginAsync();
        await ForgotAsync(email);
        var first = _api.Emails.LatestResetToken(email);
        await ForgotAsync(email);
        var second = _api.Emails.LatestResetToken(email);

        Assert.Equal(InvalidLink, await (await ResetAsync(first)).ErrorMessageAsync());
        (await ResetAsync(second)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task At_most_three_emails_per_address_per_hour_with_an_identical_response()
    {
        var (email, _) = await _api.CreateClient().RegisterAndLoginAsync();

        var bodies = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            // A different IP each time: the per-address cap must hold regardless.
            var response = await ForgotAsync(email);
            response.EnsureSuccessStatusCode();
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(3, _api.Emails.To(email).Count);
        Assert.Single(bodies.Distinct());
    }

    [Fact]
    public async Task Sixth_recovery_request_from_one_ip_within_an_hour_is_rate_limited()
    {
        var client = _api.CreateClient("10.2.0.1");

        for (var i = 0; i < RateLimitPolicies.PasswordRecoveryPermitLimit; i++)
        {
            (await ForgotAsync($"{Guid.NewGuid():N}@example.com", client)).EnsureSuccessStatusCode();
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await ForgotAsync($"{Guid.NewGuid():N}@example.com", client)).StatusCode);
    }
}
