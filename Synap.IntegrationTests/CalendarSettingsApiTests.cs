using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects;
using System.Text.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// calendar-integration tarea 1.8: `GET /api/settings` dice si Google está disponible y el estado de
/// la conexión, y nunca una credencial (specs/calendar-integration "Estado de la conexión").
/// </summary>
[Collection(ApiCollection.Name)]
public class CalendarSettingsApiTests
{
    private const string RefreshToken = "1//refresh-que-no-debe-salir";
    private const string AccessToken = "ya29.acceso-que-no-debe-salir";

    private readonly ApiFixture _api;

    public CalendarSettingsApiTests(ApiFixture api) => _api = api;

    private async Task<(HttpClient Client, Guid UserId)> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);
        return (client, await CalendarApiSupport.UserIdAsync(_api, email));
    }

    private static async Task<JsonElement> CalendarOfAsync(HttpClient client)
        => (await (await client.GetAsync("/api/settings")).ReadJsonAsync()).GetProperty("value").GetProperty("calendar");

    [Fact]
    public async Task Without_a_calendar_it_offers_google_and_shows_no_connection()
    {
        var (client, _) = await SignedInAsync();

        var calendar = await CalendarOfAsync(client);

        Assert.True(calendar.GetProperty("googleCalendarAvailable").GetBoolean());
        Assert.True(!calendar.TryGetProperty("connection", out var connection) || connection.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_connection_shows_provider_account_and_since_but_never_credentials()
    {
        var (client, userId) = await SignedInAsync();
        await CalendarApiSupport.ConnectGoogleAsync(_api, userId, "sergio@gmail.com", RefreshToken, AccessToken);

        var raw = await (await client.GetAsync("/api/settings")).Content.ReadAsStringAsync();
        var connection = (await CalendarOfAsync(client)).GetProperty("connection");

        Assert.Equal("google", connection.GetProperty("provider").GetString());
        Assert.Equal("sergio@gmail.com", connection.GetProperty("account").GetString());
        Assert.True(connection.TryGetProperty("connectedAt", out _));
        Assert.False(connection.GetProperty("needsReconnect").GetBoolean());
        Assert.DoesNotContain(RefreshToken, raw);
        Assert.DoesNotContain(AccessToken, raw);
        Assert.DoesNotContain("credentials", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_connection_marked_for_reconnect_says_so()
    {
        var (client, userId) = await SignedInAsync();
        await CalendarApiSupport.ConnectGoogleAsync(_api, userId, "sergio@gmail.com", RefreshToken, AccessToken, needsReconnect: true);

        var connection = (await CalendarOfAsync(client)).GetProperty("connection");

        Assert.True(connection.GetProperty("needsReconnect").GetBoolean());
        Assert.True(connection.TryGetProperty("needsReconnectSince", out var since) && since.ValueKind == JsonValueKind.String);
    }
}

/// <summary>Atajos para preparar conexiones directamente en la base de datos del host de pruebas.</summary>
internal static class CalendarApiSupport
{
    public static async Task<Guid> UserIdAsync(ApiFixture api, string email)
    {
        using var scope = api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SynapDbContext>();
        var address = Email.CreateFromDatabase(email);
        var user = await context.Set<User>().AsNoTracking().SingleAsync(u => u.Email == address);
        return user.Id.Value;
    }

    public static Task ConnectGoogleAsync(
        ApiFixture api, Guid userId, string account, string refreshToken, string accessToken, bool needsReconnect = false)
        => ConnectAsync(api, userId, CalendarProvider.Google, account, "primary",
            new CalendarCredentials.Google(refreshToken, accessToken, DateTime.UtcNow.AddHours(1)), needsReconnect);

    public static Task ConnectCalDavAsync(ApiFixture api, Guid userId, string account, string calendarUrl, string password)
        => ConnectAsync(api, userId, CalendarProvider.CalDav, account, calendarUrl,
            new CalendarCredentials.CalDav(account, password), needsReconnect: false);

    public static async Task<CalendarConnection?> ConnectionOfAsync(ApiFixture api, Guid userId)
    {
        using var scope = api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SynapDbContext>();
        var id = Synap.Shared.Domain.ValueObjects.Ids.UserId.CreateFromDatabase(userId);
        return await context.Set<CalendarConnection>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
    }

    public static CalendarCredentials? CredentialsOf(ApiFixture api, CalendarConnection connection)
    {
        using var scope = api.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CalendarCredentialsProtector>()
            .TryRead(connection.Provider, connection.CredentialsEncrypted);
    }

    private static async Task ConnectAsync(
        ApiFixture api, Guid userId, CalendarProvider provider, string account, string calendarRef,
        CalendarCredentials credentials, bool needsReconnect)
    {
        using var scope = api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SynapDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<CalendarCredentialsProtector>();

        var connection = CalendarConnection.Create(
            userId, provider, account, calendarRef, protector.Protect(credentials), DateTime.UtcNow);
        if (needsReconnect)
        {
            connection.MarkNeedsReconnect(DateTime.UtcNow);
        }

        context.Set<CalendarConnection>().Add(connection);
        await context.SaveChangesAsync();
    }
}
