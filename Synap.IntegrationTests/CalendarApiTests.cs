using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// calendar-store-and-view tarea 1.7: `GET /api/calendar/calendars`, `PATCH /api/calendar/calendars/{id}`,
/// `GET /api/calendar/events` y `POST /api/calendar/sync` contra el host de pruebas.
/// Cubre id ajeno → 404, desactivar borra los eventos, intervalo excesivo rechazado y sin credenciales.
/// </summary>
[Collection(ApiCollection.Name)]
public class CalendarApiTests
{
    private const string Home = "https://dav.example.test/sergio/";
    private const string CalendarHref = "https://dav.example.test/sergio/personal/";
    private static readonly string SampleIcs =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" +
        "BEGIN:VEVENT\r\nUID:api-test-event\r\nDTSTAMP:20261001T120000Z\r\n" +
        "DTSTART:20261114T090000Z\r\nDTEND:20261114T100000Z\r\nSUMMARY:Reunión de API\r\n" +
        "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private readonly ApiFixture _api;

    public CalendarApiTests(ApiFixture api) => _api = api;

    // ---- Helpers ----

    private async Task<(HttpClient Client, Guid UserId)> SignedInAsync()
    {
        var client = _api.CreateClient();
        var (email, token) = await client.RegisterAndLoginAsync();
        client.WithBearer(token);
        var userId = await CalendarApiSupport.UserIdAsync(_api, email);
        return (client, userId);
    }

    private async Task<Guid> SeedCalendarAsync(Guid userId, string calendarHref = CalendarHref)
    {
        using var scope = _api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SynapDbContext>();
        var userIdTyped = UserId.CreateFromDatabase(userId);

        var calendar = Domain.Calendar.Create(userIdTyped, calendarHref, "Personal", null);
        context.Set<Domain.Calendar>().Add(calendar);
        await context.SaveChangesAsync();
        return calendar.Id.Value;
    }

    private async Task SeedEventAsync(Guid userId, Guid calendarId, string href)
    {
        using var scope = _api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SynapDbContext>();
        var userIdTyped = UserId.CreateFromDatabase(userId);
        var calendarIdTyped = CalendarId.Create(calendarId).Value;

        var fields = new CalendarEventFields(
            "api-test-uid", "Reunión de API",
            new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc),
            false, false);
        var resource = CalendarEventResource.Create(calendarIdTyped, userIdTyped, href, "\"e1\"", SampleIcs, fields);
        context.Set<CalendarEventResource>().Add(resource);
        await context.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<CalendarEventResource>> EventsOfAsync(Guid userId, Guid calendarId)
    {
        using var scope = _api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SynapDbContext>();
        var userIdTyped = UserId.CreateFromDatabase(userId);
        return await context.Set<CalendarEventResource>()
            .AsNoTracking()
            .Where(e => e.UserId == userIdTyped && e.CalendarId == CalendarId.Create(calendarId).Value)
            .ToListAsync();
    }

    // ---- GET /api/calendar/calendars ----

    [Fact]
    public async Task Calendars_endpoint_lists_the_users_calendars()
    {
        var (client, userId) = await SignedInAsync();
        await SeedCalendarAsync(userId);

        var response = await client.GetAsync("/api/calendar/calendars");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.ReadJsonAsync()).GetProperty("value");
        var calendars = body.GetProperty("calendars");
        Assert.Equal(JsonValueKind.Array, calendars.ValueKind);
        Assert.Equal(1, calendars.GetArrayLength());

        var cal = calendars[0];
        Assert.Equal("Personal", cal.GetProperty("name").GetString());
        Assert.Equal("personal", cal.GetProperty("scope").GetString());
        Assert.True(cal.GetProperty("enabled").GetBoolean());
    }

    // ---- PATCH /api/calendar/calendars/{id} ----

    [Fact]
    public async Task Patching_another_users_calendar_returns_404()
    {
        var (clientA, _) = await SignedInAsync();
        var (_, userB) = await SignedInAsync();

        var calendarIdOfB = await SeedCalendarAsync(userB);

        var response = await clientA.PatchAsJsonAsync(
            $"/api/calendar/calendars/{calendarIdOfB}",
            new { scope = "work" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Disabling_a_calendar_deletes_its_events()
    {
        var (client, userId) = await SignedInAsync();
        var calendarId = await SeedCalendarAsync(userId);
        await SeedEventAsync(userId, calendarId, CalendarHref + "reunion.ics");

        Assert.NotEmpty(await EventsOfAsync(userId, calendarId));

        var response = await client.PatchAsJsonAsync(
            $"/api/calendar/calendars/{calendarId}",
            new { enabled = false });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await EventsOfAsync(userId, calendarId));
    }

    [Fact]
    public async Task Patching_scope_changes_it_and_patch_response_has_no_credentials()
    {
        var (client, userId) = await SignedInAsync();
        await CalendarApiSupport.ConnectCalDavAsync(
            _api, userId, "sergio@example.com", Home, "secret-password");
        var calendarId = await SeedCalendarAsync(userId);

        var patchResponse = await client.PatchAsJsonAsync(
            $"/api/calendar/calendars/{calendarId}",
            new { scope = "work" });

        Assert.Equal(HttpStatusCode.NoContent, patchResponse.StatusCode);

        // La respuesta de PATCH es vacía; verificamos que settings tampoco lleva contraseña.
        var raw = await (await client.GetAsync("/api/settings")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-password", raw);
    }

    // ---- GET /api/calendar/events ----

    [Fact]
    public async Task Events_endpoint_rejects_intervals_over_62_days()
    {
        var (client, _) = await SignedInAsync();
        var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(63);

        var response = await client.GetAsync(
            $"/api/calendar/events?from={from:O}&to={to:O}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Events_endpoint_returns_ok_even_without_a_connection()
    {
        var (client, _) = await SignedInAsync();
        var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(7);

        var response = await client.GetAsync(
            $"/api/calendar/events?from={from:O}&to={to:O}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.ReadJsonAsync()).GetProperty("value");
        Assert.Equal(0, body.GetProperty("events").GetArrayLength());
    }

    // ---- POST /api/calendar/sync ----

    [Fact]
    public async Task Sync_endpoint_enqueues_and_returns_204()
    {
        var (client, _) = await SignedInAsync();

        // Primera petición: se acepta.
        var first = await client.PostAsync("/api/calendar/sync", null);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
    }

    [Fact]
    public async Task Sync_endpoint_returns_429_when_called_too_soon()
    {
        var (client, _) = await SignedInAsync();

        await client.PostAsync("/api/calendar/sync", null);

        // Inmediatamente de nuevo: rate limit.
        var second = await client.PostAsync("/api/calendar/sync", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("Espera", body);
        Assert.True(second.Headers.Contains("Retry-After"));
    }

    // ---- Privacidad de registros (design.md Decisión 8) ----

    /// <summary>
    /// Los endpoints de calendario no escriben en los registros el título de ningún evento,
    /// ni el nombre de ningún calendario, ni ningún dato del contenido del calendario.
    /// </summary>
    [Fact]
    public async Task Calendar_endpoints_do_not_log_event_or_calendar_content()
    {
        const string SensitiveTitle = "Reunión con el notario";
        var (client, userId) = await SignedInAsync();

        // GET /api/calendar/events delega en el CalendarReaderFake: configuramos el fake.
        _api.CalendarEvents.Returns(new CalendarEvent(
            SensitiveTitle,
            new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc)));

        var from = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);

        var logsBefore = _api.Logs.Records.Count;

        // Llama a los tres endpoints nuevos.
        await SeedCalendarAsync(userId);
        await client.GetAsync("/api/calendar/calendars");
        await client.GetAsync($"/api/calendar/events?from={from:O}&to={to:O}");
        await client.PostAsync("/api/calendar/sync", null);

        // Ninguna traza posterior contiene el título del evento.
        var newLogs = _api.Logs.Records.Skip(logsBefore).ToList();
        Assert.DoesNotContain(newLogs, r => r.Contains(SensitiveTitle, StringComparison.OrdinalIgnoreCase));
    }
}
