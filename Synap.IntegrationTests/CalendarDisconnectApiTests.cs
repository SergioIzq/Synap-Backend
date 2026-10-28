using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>calendar-integration tarea 1.11: `DELETE /api/settings/calendar` (specs "Desconectar el calendario").</summary>
[Collection(ApiCollection.Name)]
public class CalendarDisconnectApiTests
{
    private readonly ApiFixture _api;

    public CalendarDisconnectApiTests(ApiFixture api) => _api = api;

    private async Task<(HttpClient Client, Guid UserId)> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);
        return (client, await CalendarApiSupport.UserIdAsync(_api, email));
    }

    [Fact]
    public async Task Disconnecting_caldav_deletes_the_connection()
    {
        var (client, userId) = await SignedInAsync();
        await CalendarApiSupport.ConnectCalDavAsync(_api, userId, "sergio@fastmail.com", "https://caldav.fastmail.com/cal/", "pass");

        var response = await client.DeleteAsync("/api/settings/calendar");

        response.EnsureSuccessStatusCode();
        Assert.Null(await CalendarApiSupport.ConnectionOfAsync(_api, userId));
    }

    /// <summary>specs "Recordatorios conservados".</summary>
    [Fact]
    public async Task Reminders_stay_pending_after_disconnecting()
    {
        var (client, userId) = await SignedInAsync();
        await CalendarApiSupport.ConnectCalDavAsync(_api, userId, "sergio", "https://caldav.example.com/cal/", "secreto");
        var created = await client.PostAsJsonAsync(
            "/api/reminders", new { text = "Revisar la presentación", dueAtUtc = DateTime.UtcNow.AddDays(2) });
        created.EnsureSuccessStatusCode();

        (await client.DeleteAsync("/api/settings/calendar")).EnsureSuccessStatusCode();

        var reminders = (await (await client.GetAsync("/api/reminders")).ReadJsonAsync()).GetProperty("value").GetProperty("reminders");
        var reminder = Assert.Single(reminders.EnumerateArray());
        Assert.Equal("Revisar la presentación", reminder.GetProperty("text").GetString());
        Assert.Null(await CalendarApiSupport.ConnectionOfAsync(_api, userId));
    }

    [Fact]
    public async Task Disconnecting_without_a_calendar_is_not_an_error()
    {
        var (client, _) = await SignedInAsync();

        var response = await client.DeleteAsync("/api/settings/calendar");

        response.EnsureSuccessStatusCode();
        var calendar = (await response.ReadJsonAsync()).GetProperty("value");
        Assert.True(!calendar.TryGetProperty("connection", out var c) || c.ValueKind == JsonValueKind.Null);
    }
}
