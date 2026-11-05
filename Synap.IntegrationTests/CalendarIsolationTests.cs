using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Data.Calendar;
using Synap.Infrastructure.Persistence.Data.Users;
using Synap.Infrastructure.Services.Calendar;
using Synap.Infrastructure.Services.Secrets;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;
using System.Security.Cryptography;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// calendar-integration tareas 1.2 y 1.3 contra un Postgres real: la tabla `calendar_connections`
/// se borra con el usuario, las credenciales se guardan cifradas, y leer de la copia de un usuario
/// nunca devuelve eventos de otro (specs/calendar-integration "Copia aislada entre usuarios").
/// </summary>
[Collection(PostgresCollection.Name)]
public class CalendarIsolationTests
{
    private static readonly DateTime DayStartUtc = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DayEndUtc = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);

    private const string UserACalendar = "https://solo-de-a.example.test/dav/calendars/a/default/";
    private const string UserAPassword = "la-contrasena-de-a";

    private readonly PostgresFixture _fixture;
    private readonly ISecretProtector _protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));

    public CalendarIsolationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task One_users_calendar_copy_is_never_read_for_another()
    {
        var (userA, userB) = await CreateUsersAsync();
        await ConnectAsync(userA);

        await using var setupContext = _fixture.CreateContext();

        // Guardadas y cifradas: ni la contraseña ni el usuario están en la columna.
        var stored = await setupContext.Set<CalendarConnection>().AsNoTracking().SingleAsync(c => c.Id == UserIdOf(userA));
        Assert.DoesNotContain(UserAPassword, stored.CredentialsEncrypted);
        Assert.Equal(UserACalendar, stored.CalendarRef);

        await using var readContext = _fixture.CreateContext();
        var reader = new CalendarReader(readContext, NullLogger<CalendarReader>.Instance);

        // Sin conexión: nunca devuelve nada de otro usuario ni de uno inexistente.
        Assert.IsType<CalendarReadResult.NotConnected>(await reader.ListEventsAsync(userB, DayStartUtc, DayEndUtc, 10));
        Assert.IsType<CalendarReadResult.NotConnected>(await reader.ListEventsAsync(Guid.NewGuid(), DayStartUtc, DayEndUtc, 10));

        // Con conexión pero sin copia: devuelve Ok vacío (la copia existe, simplemente no tiene eventos todavía).
        Assert.IsType<CalendarReadResult.Ok>(await reader.ListEventsAsync(userA, DayStartUtc, DayEndUtc, 10));
    }

    /// <summary>
    /// El estado que lee Configuración no lleva credenciales, y el de un usuario no dice nada del de otro.
    /// </summary>
    [Fact]
    public async Task The_connection_state_of_one_user_says_nothing_about_another()
    {
        var (userA, userB) = await CreateUsersAsync();
        await ConnectAsync(userA);

        await using var context = _fixture.CreateContext();
        var read = new CalendarConnectionReadRepository(context);

        var ofA = await read.GetSummaryAsync(userA);
        Assert.Equal(CalendarProvider.CalDav, ofA!.Provider);
        Assert.Equal("a@example.test", ofA.AccountLabel);
        Assert.Null(ofA.NeedsReconnectSince);

        Assert.Null(await read.GetSummaryAsync(userB));
    }

    /// <summary>calendar-integration tarea 1.2 / specs "Cuenta borrada": el cascade borra la conexión.</summary>
    [Fact]
    public async Task Deleting_the_user_deletes_the_connection()
    {
        var (userA, userB) = await CreateUsersAsync();
        await ConnectAsync(userA);
        await ConnectAsync(userB, "b@example.test");

        await using (var context = _fixture.CreateContext())
        {
            await new UserWriteRepository(context).DeleteWithAllDataAsync(userA);
        }

        await using (var context = _fixture.CreateContext())
        {
            Assert.False(await context.Set<CalendarConnection>().AnyAsync(c => c.Id == UserIdOf(userA)));
            Assert.True(await context.Set<CalendarConnection>().AnyAsync(c => c.Id == UserIdOf(userB)));
        }
    }

    private static Synap.Shared.Domain.ValueObjects.Ids.UserId UserIdOf(Guid id) =>
        Synap.Shared.Domain.ValueObjects.Ids.UserId.CreateFromDatabase(id);

    private async Task ConnectAsync(Guid userId, string accountLabel = "a@example.test")
    {
        await using var context = _fixture.CreateContext();
        context.Set<CalendarConnection>().Add(CalendarConnection.Create(
            userId,
            CalendarProvider.CalDav,
            accountLabel,
            UserACalendar,
            new CalendarCredentialsProtector(_protector).Protect(new CalendarCredentials.CalDav("a", UserAPassword)),
            DateTime.UtcNow));
        await context.SaveChangesAsync();
    }

    /// <summary>calendar-store-and-view tarea 1.9: un calendario desactivado no devuelve eventos en el briefing.</summary>
    [Fact]
    public async Task A_disabled_calendars_events_are_absent_from_the_read()
    {
        await using var context = _fixture.CreateContext();
        var user = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        context.Set<User>().Add(user);
        await context.SaveChangesAsync();

        var userId = user.Id;
        context.Set<CalendarConnection>().Add(CalendarConnection.Create(
            userId.Value, CalendarProvider.CalDav, "u@example.test", UserACalendar,
            new CalendarCredentialsProtector(_protector).Protect(new CalendarCredentials.CalDav("u", UserAPassword)),
            DateTime.UtcNow));

        // Calendario activo con un evento.
        var activeCalendar = Calendar.Create(userId, UserACalendar + "personal/", "Personal", null);
        context.Set<Calendar>().Add(activeCalendar);
        await context.SaveChangesAsync();

        var fields = new CalendarEventFields("uid-1", "Reunión", new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 7, 10, 0, 0, DateTimeKind.Utc), false, false);
        context.Set<CalendarEventResource>().Add(
            CalendarEventResource.Create(activeCalendar.Id, userId,
                UserACalendar + "personal/meeting.ics", "\"e1\"", "BEGIN:VCALENDAR\r\nEND:VCALENDAR", fields));
        await context.SaveChangesAsync();

        // Deshabilitar el calendario elimina los eventos; el lector no debe devolver ninguno.
        await using var disableContext = _fixture.CreateContext();
        var calendarToDisable = await disableContext.Set<Calendar>().SingleAsync(c => c.Id == activeCalendar.Id);
        calendarToDisable.Disable();
        await disableContext.SaveChangesAsync();

        // El lector ya no devuelve eventos del calendario desactivado.
        await using var readContext = _fixture.CreateContext();
        var reader = new CalendarReader(readContext, NullLogger<CalendarReader>.Instance);
        var result = await reader.ListEventsAsync(userId.Value, DayStartUtc, DayEndUtc, 10);
        // Con conexión pero sin calendarios activos: Ok vacío.
        var ok = Assert.IsType<CalendarReadResult.Ok>(result);
        Assert.Empty(ok.Events);
    }

    private async Task<(Guid UserA, Guid UserB)> CreateUsersAsync()
    {
        await using var context = _fixture.CreateContext();

        var userA = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        var userB = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        context.Set<User>().AddRange(userA, userB);
        await context.SaveChangesAsync();

        return (userA.Id.Value, userB.Id.Value);
    }
}
