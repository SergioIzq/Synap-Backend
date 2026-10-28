using System.Net;
using System.Net.Http.Json;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// calendar-integration tarea 1.10: `PUT /api/settings/calendar/caldav` y los cuatro fallos de
/// specs/calendar-integration "Conectar un calendario CalDAV", ninguno de los cuales toca la
/// conexión anterior.
/// </summary>
[Collection(ApiCollection.Name)]
public class CalDavConnectApiTests
{
    private const string Server = "https://caldav.fastmail.test/dav/";

    private readonly ApiFixture _api;

    public CalDavConnectApiTests(ApiFixture api) => _api = api;

    private async Task<(HttpClient Client, Guid UserId)> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);
        return (client, await CalendarApiSupport.UserIdAsync(_api, email));
    }

    private static Task<HttpResponseMessage> ConnectAsync(HttpClient client, string url, string username, string password)
        => client.PutAsJsonAsync("/api/settings/calendar/caldav", new { url, username, password });

    [Fact]
    public async Task A_valid_server_is_connected_and_shown()
    {
        var (client, userId) = await SignedInAsync();

        var response = await ConnectAsync(client, Server, "sergio@fastmail.test", "app-pass");

        response.EnsureSuccessStatusCode();
        var connection = (await response.ReadJsonAsync()).GetProperty("value").GetProperty("connection");
        Assert.Equal("caldav", connection.GetProperty("provider").GetString());
        Assert.Equal("sergio@fastmail.test", connection.GetProperty("account").GetString());
        Assert.DoesNotContain("app-pass", await response.Content.ReadAsStringAsync());

        var stored = await CalendarApiSupport.ConnectionOfAsync(_api, userId);
        Assert.Equal($"{Server}calendars/default/", stored!.CalendarRef);
        Assert.Equal(
            new CalendarCredentials.CalDav("sergio@fastmail.test", "app-pass"),
            CalendarApiSupport.CredentialsOf(_api, stored));
    }

    [Theory]
    [InlineData("rechazada", "rechazado el usuario o la contraseña")]
    [InlineData("caida", "No se ha podido contactar con el servidor")]
    [InlineData("vacia", "no se ha encontrado ningún calendario")]
    public async Task Each_failure_has_its_message_and_keeps_the_previous_connection(string password, string message)
    {
        var (client, userId) = await SignedInAsync();
        await CalendarApiSupport.ConnectCalDavAsync(_api, userId, "sergio@fastmail.com", "https://caldav.fastmail.com/prev/", "prev-pass");

        var response = await ConnectAsync(client, Server, "sergio", password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await response.ErrorMessageAsync());
        var connection = await CalendarApiSupport.ConnectionOfAsync(_api, userId);
        Assert.Equal(CalendarProvider.CalDav, connection!.Provider);
        Assert.Equal("sergio@fastmail.com", connection.AccountLabel);
    }

    /// <summary>specs "URL sin HTTPS": se rechaza sin contactar con el servidor.</summary>
    [Fact]
    public async Task A_url_without_https_is_refused_without_contacting_the_server()
    {
        var (client, userId) = await SignedInAsync();
        var url = $"http://{Guid.NewGuid():N}.example.test/dav/";

        var response = await ConnectAsync(client, url, "sergio", "app-pass");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("https://", await response.ErrorMessageAsync());
        Assert.DoesNotContain(_api.CalDav.Contacted, u => u.AbsoluteUri == url);
        Assert.Null(await CalendarApiSupport.ConnectionOfAsync(_api, userId));
    }

    [Fact]
    public async Task Missing_credentials_are_refused()
    {
        var (client, _) = await SignedInAsync();

        var response = await ConnectAsync(client, Server, "sergio", "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Reconectar quita la marca (specs "Reconexión").</summary>
    [Fact]
    public async Task Connecting_again_clears_the_reconnect_mark()
    {
        var (client, userId) = await SignedInAsync();
        await CalendarApiSupport.ConnectCalDavAsync(_api, userId, "sergio@fastmail.com", "https://caldav.fastmail.com/prev/", "prev-pass", needsReconnect: true);

        (await ConnectAsync(client, Server, "sergio", "app-pass")).EnsureSuccessStatusCode();

        var connection = await CalendarApiSupport.ConnectionOfAsync(_api, userId);
        Assert.Equal(CalendarProvider.CalDav, connection!.Provider);
        Assert.Null(connection.NeedsReconnectSince);
    }
}
