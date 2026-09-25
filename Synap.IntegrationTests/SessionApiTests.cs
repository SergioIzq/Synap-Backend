using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>password-recovery tasks 2.1-2.3 - specs/identity "Password changes end other sessions".</summary>
[Collection(ApiCollection.Name)]
public class SessionApiTests
{
    private readonly ApiFixture _api;

    public SessionApiTests(ApiFixture api) => _api = api;

    [Fact]
    public async Task Session_token_carries_the_security_stamp_and_works()
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(32, jwt.Claims.Single(c => c.Type == "stamp").Value.Length);
        Assert.Contains(jwt.Claims, c => c.Type == "iat");

        Assert.Equal(HttpStatusCode.OK, (await client.WithBearer(token).GetAsync("/api/users/me")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_ends_other_sessions_keeps_the_callers_and_the_api_token()
    {
        var device1 = _api.CreateClient();
        var (email, token1) = await device1.RegisterAndLoginAsync();
        var device2 = _api.CreateClient().WithBearer(await _api.CreateClient().LoginAsync(email, ApiClientExtensions.Password));
        device1.WithBearer(token1);
        var apiToken = (await (await device1.PostAsync("/api/auth/api-token", null)).ReadJsonAsync()).GetProperty("value").GetString()!;

        var response = await device1.PutAsJsonAsync("/api/users/me/password",
            new { currentPassword = ApiClientExtensions.Password, newPassword = "OtraClave-2026" });

        response.EnsureSuccessStatusCode();
        var newToken = (await response.ReadJsonAsync()).GetProperty("value").GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await device2.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device1.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.CreateClient().WithBearer(newToken).GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.CreateClient().WithBearer(apiToken).GetAsync("/api/notes/search")).StatusCode);
    }

    [Fact]
    public async Task A_token_without_a_stamp_is_rejected()
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();

        // Same signature key and claims as a real token, minus the stamp - like the tokens
        // issued before this change was deployed.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var claims = jwt.Claims.Where(c => c.Type is not ("stamp" or "exp" or "nbf" or "iat" or "aud" or "iss"));
        var unstamped = ApiFixture.SignToken(claims);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.CreateClient().WithBearer(unstamped).GetAsync("/api/users/me")).StatusCode);
    }
}
