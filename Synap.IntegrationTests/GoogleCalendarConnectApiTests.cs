using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using System.Net;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// calendar-integration tarea 1.9: el inicio y la vuelta de Google por HTTP (design.md Decisión 3;
/// specs/calendar-integration "Conectar Google Calendar").
/// </summary>
[Collection(ApiCollection.Name)]
public class GoogleCalendarConnectApiTests
{
    private readonly ApiFixture _api;

    public GoogleCalendarConnectApiTests(ApiFixture api) => _api = api;

    private async Task<(HttpClient Client, Guid UserId)> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);
        return (client, await CalendarApiSupport.UserIdAsync(_api, email));
    }

    private static async Task<string> StartAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/settings/calendar/google/start", null);
        response.EnsureSuccessStatusCode();
        var url = (await response.ReadJsonAsync()).GetProperty("value").GetProperty("authorizationUrl").GetString()!;
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Contains("calendar.events.readonly", url);
        return query["state"]!;
    }

    private async Task<string?> CallbackAsync(string query)
    {
        var response = await _api.CreateClientWithoutRedirects().GetAsync($"/api/calendar/google/callback?{query}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith($"{ApiFixture.PublicBaseUrl}/app/settings?calendar=", location);
        return location[(location.IndexOf('=') + 1)..];
    }

    private static string Code() => $"ok-{Guid.NewGuid():N}";

    [Fact]
    public async Task Granting_access_connects_the_primary_calendar()
    {
        var (client, userId) = await SignedInAsync();
        var state = await StartAsync(client);
        var code = Code();

        var outcome = await CallbackAsync($"code={code}&state={Uri.EscapeDataString(state)}");

        Assert.Equal("connected", outcome);
        var connection = await CalendarApiSupport.ConnectionOfAsync(_api, userId);
        Assert.Equal(CalendarProvider.Google, connection!.Provider);
        Assert.Equal($"{code}@gmail.com", connection.AccountLabel);
        Assert.Equal("primary", connection.CalendarRef);
        var credentials = Assert.IsType<CalendarCredentials.Google>(CalendarApiSupport.CredentialsOf(_api, connection));
        Assert.Equal($"refresh-{code}", credentials.RefreshToken);
        Assert.DoesNotContain($"refresh-{code}", connection.CredentialsEncrypted);
    }

    /// <summary>specs "Respuesta de Google ajena o caducada": un `state` caducado no guarda nada.</summary>
    [Fact]
    public async Task An_expired_state_saves_nothing()
    {
        var (client, userId) = await SignedInAsync();
        var state = await StartAsync(client);
        await ExpireOAuthStartAsync(userId);
        var code = Code();

        var outcome = await CallbackAsync($"code={code}&state={Uri.EscapeDataString(state)}");

        Assert.Equal("error", outcome);
        Assert.Null(await CalendarApiSupport.ConnectionOfAsync(_api, userId));
        Assert.DoesNotContain(code, _api.GoogleCalendar.Exchanged);
    }

    [Fact]
    public async Task A_state_that_expired_in_its_own_payload_saves_nothing()
    {
        var (client, userId) = await SignedInAsync();
        await StartAsync(client);
        string forged;
        using (var scope = _api.Services.CreateScope())
        {
            forged = scope.ServiceProvider.GetRequiredService<GoogleOAuthState>()
                .Issue(userId, "nonce-cualquiera", DateTime.UtcNow.AddMinutes(-1));
        }

        var outcome = await CallbackAsync($"code={Code()}&state={Uri.EscapeDataString(forged)}");

        Assert.Equal("error", outcome);
        Assert.Null(await CalendarApiSupport.ConnectionOfAsync(_api, userId));
    }

    /// <summary>design.md Decisión 3: el nonce es de un solo uso, así que una respuesta capturada no se repite.</summary>
    [Fact]
    public async Task A_reused_nonce_saves_nothing_the_second_time()
    {
        var (client, userId) = await SignedInAsync();
        var state = await StartAsync(client);
        var first = Code();
        var second = Code();

        Assert.Equal("connected", await CallbackAsync($"code={first}&state={Uri.EscapeDataString(state)}"));
        Assert.Equal("error", await CallbackAsync($"code={second}&state={Uri.EscapeDataString(state)}"));

        var connection = await CalendarApiSupport.ConnectionOfAsync(_api, userId);
        Assert.Equal($"{first}@gmail.com", connection!.AccountLabel);
        Assert.DoesNotContain(second, _api.GoogleCalendar.Exchanged);
    }

    [Fact]
    public async Task A_state_that_is_not_ours_saves_nothing()
    {
        var outcome = await CallbackAsync($"code={Code()}&state=esto-no-es-un-state");

        Assert.Equal("error", outcome);
    }

    /// <summary>specs "Permiso denegado": no se guarda nada y se conserva la conexión anterior.</summary>
    [Fact]
    public async Task Denying_access_keeps_the_previous_connection()
    {
        var (client, userId) = await SignedInAsync();
        await CalendarApiSupport.ConnectCalDavAsync(_api, userId, "sergio", "https://caldav.example.com/cal/", "secreto");
        var state = await StartAsync(client);

        var outcome = await CallbackAsync($"error=access_denied&state={Uri.EscapeDataString(state)}");

        Assert.Equal("denied", outcome);
        var connection = await CalendarApiSupport.ConnectionOfAsync(_api, userId);
        Assert.Equal(CalendarProvider.CalDav, connection!.Provider);
    }

    /// <summary>specs "Cambio de proveedor": la conexión nueva reemplaza a la anterior y sus credenciales.</summary>
    [Fact]
    public async Task A_new_connection_replaces_the_previous_one()
    {
        var (client, userId) = await SignedInAsync();
        await CalendarApiSupport.ConnectCalDavAsync(_api, userId, "sergio", "https://caldav.example.com/cal/", "secreto");
        var state = await StartAsync(client);

        Assert.Equal("connected", await CallbackAsync($"code={Code()}&state={Uri.EscapeDataString(state)}"));

        var connection = await CalendarApiSupport.ConnectionOfAsync(_api, userId);
        Assert.Equal(CalendarProvider.Google, connection!.Provider);
        Assert.IsType<CalendarCredentials.Google>(CalendarApiSupport.CredentialsOf(_api, connection));
        Assert.Null(connection.NeedsReconnectSince);
    }

    [Fact]
    public async Task A_code_google_refuses_saves_nothing()
    {
        var (client, userId) = await SignedInAsync();
        var state = await StartAsync(client);

        var outcome = await CallbackAsync($"code=caducado&state={Uri.EscapeDataString(state)}");

        Assert.Equal("error", outcome);
        Assert.Null(await CalendarApiSupport.ConnectionOfAsync(_api, userId));
    }

    private async Task ExpireOAuthStartAsync(Guid userId)
    {
        await using var connection = await _api.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE users SET calendar_oauth_expires_at = calendar_oauth_expires_at - interval '11 minutes' WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", userId);
        await command.ExecuteNonQueryAsync();
    }
}
