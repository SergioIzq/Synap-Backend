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
    public async Task Disconnecting_google_deletes_the_connection_and_revokes_the_token()
    {
        var (client, userId) = await SignedInAsync();
        var refresh = $"refresh-{Guid.NewGuid():N}";
        await CalendarApiSupport.ConnectGoogleAsync(_api, userId, "sergio@gmail.com", refresh, "access");

        var response = await client.DeleteAsync("/api/settings/calendar");

        response.EnsureSuccessStatusCode();
        Assert.Null(await CalendarApiSupport.ConnectionOfAsync(_api, userId));
        Assert.Contains(refresh, _api.GoogleCalendar.Revoked);
    }

    /// <summary>specs "Revocación fallida en Google": se borra igual y queda registrado.</summary>
    [Fact]
    public async Task A_failed_revocation_does_not_block_the_disconnection()
    {
        var (client, userId) = await SignedInAsync();
        var refresh = $"refresh-{Guid.NewGuid():N}";
        _api.GoogleCalendar.RevocationFails[refresh] = true;
        await CalendarApiSupport.ConnectGoogleAsync(_api, userId, "sergio@gmail.com", refresh, "access");

        var response = await client.DeleteAsync("/api/settings/calendar");

        response.EnsureSuccessStatusCode();
        Assert.Null(await CalendarApiSupport.ConnectionOfAsync(_api, userId));
        Assert.Contains(_api.Logs.Records, m => m.Contains("revocar") && m.Contains(userId.ToString()));
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
