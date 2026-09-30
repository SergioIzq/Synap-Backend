using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>daily-briefing task 1.4 - specs/briefing "The briefing is off until the user turns it on" over HTTP.</summary>
[Collection(ApiCollection.Name)]
public class BriefingSettingsApiTests
{
    private readonly ApiFixture _api;

    public BriefingSettingsApiTests(ApiFixture api) => _api = api;

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);
        return client;
    }

    private static async Task<System.Text.Json.JsonElement> BriefingOfAsync(HttpClient client)
        => (await (await client.GetAsync("/api/settings")).ReadJsonAsync()).GetProperty("value").GetProperty("briefing");

    /// <summary>No hour chosen: the API leaves the property out rather than writing a null.</summary>
    private static void AssertNoHour(System.Text.Json.JsonElement briefing)
        => Assert.True(
            !briefing.TryGetProperty("hour", out var hour) || hour.ValueKind == System.Text.Json.JsonValueKind.Null,
            $"expected no hour, got {briefing}");

    [Fact]
    public async Task A_new_account_has_the_briefing_off()
    {
        var briefing = await BriefingOfAsync(await SignedInAsync());

        Assert.False(briefing.GetProperty("enabled").GetBoolean());
        AssertNoHour(briefing);
        // No Telegram linked yet, so it could not be delivered even if it were on.
        Assert.False(briefing.GetProperty("canBeDelivered").GetBoolean());
    }

    [Fact]
    public async Task Turning_it_on_round_trips_through_the_settings()
    {
        var client = await SignedInAsync();

        var saved = await (await client.PutAsJsonAsync("/api/settings/briefing", new { enabled = true, hour = 7 })).ReadJsonAsync();

        Assert.Equal((true, 7), (
            saved.GetProperty("value").GetProperty("enabled").GetBoolean(),
            saved.GetProperty("value").GetProperty("hour").GetInt32()));

        var briefing = await BriefingOfAsync(client);
        Assert.Equal((true, 7), (briefing.GetProperty("enabled").GetBoolean(), briefing.GetProperty("hour").GetInt32()));
    }

    [Fact]
    public async Task Turning_it_off_keeps_the_hour()
    {
        var client = await SignedInAsync();
        (await client.PutAsJsonAsync("/api/settings/briefing", new { enabled = true, hour = 7 })).EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/settings/briefing", new { enabled = false, hour = (int?)null })).EnsureSuccessStatusCode();

        var briefing = await BriefingOfAsync(client);
        Assert.False(briefing.GetProperty("enabled").GetBoolean());
        Assert.Equal(7, briefing.GetProperty("hour").GetInt32());
    }

    [Fact]
    public async Task An_hour_outside_the_day_is_refused()
    {
        var client = await SignedInAsync();

        var response = await client.PutAsJsonAsync("/api/settings/briefing", new { enabled = true, hour = 24 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await BriefingOfAsync(client)).GetProperty("enabled").GetBoolean());
    }

    /// <summary>specs/briefing "One user's setting is not another's".</summary>
    [Fact]
    public async Task One_users_briefing_setting_never_reaches_another()
    {
        var mine = await SignedInAsync();
        var theirs = await SignedInAsync();

        (await mine.PutAsJsonAsync("/api/settings/briefing", new { enabled = true, hour = 6 })).EnsureSuccessStatusCode();

        var other = await BriefingOfAsync(theirs);
        Assert.False(other.GetProperty("enabled").GetBoolean());
        AssertNoHour(other);
    }

    [Fact]
    public async Task The_briefing_settings_need_authentication()
    {
        var anonymous = _api.CreateClient();

        var read = await anonymous.GetAsync("/api/settings");
        var write = await anonymous.PutAsJsonAsync("/api/settings/briefing", new { enabled = true, hour = 9 });

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }
}
