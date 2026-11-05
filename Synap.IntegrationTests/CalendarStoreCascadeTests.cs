using Microsoft.EntityFrameworkCore;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Data.Users;
using Synap.Shared.Domain.ValueObjects;
using Synap.Shared.Domain.ValueObjects.Ids;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// calendar-store-and-view tarea 1.1 contra un Postgres real: la copia cuelga de la conexión, así
/// que borrar la conexión o el usuario borra en cascada calendarios y eventos
/// (specs/calendar-integration "La copia se borra con la conexión").
/// </summary>
[Collection(PostgresCollection.Name)]
public class CalendarStoreCascadeTests
{
    private readonly PostgresFixture _fixture;

    public CalendarStoreCascadeTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Deleting_the_connection_deletes_calendars_and_events()
    {
        var userA = await CreateUserWithCopyAsync();
        var userB = await CreateUserWithCopyAsync();

        await using (var context = _fixture.CreateContext())
        {
            var connection = await context.Set<CalendarConnection>().SingleAsync(c => c.Id == Id(userA));
            context.Remove(connection);
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            Assert.False(await context.Set<Calendar>().AnyAsync(c => c.UserId == Id(userA)));
            Assert.False(await context.Set<CalendarEventResource>().AnyAsync(e => e.UserId == Id(userA)));
            Assert.Equal(2, await context.Set<Calendar>().CountAsync(c => c.UserId == Id(userB)));
            Assert.Equal(2, await context.Set<CalendarEventResource>().CountAsync(e => e.UserId == Id(userB)));
        }
    }

    [Fact]
    public async Task Deleting_the_user_deletes_calendars_and_events()
    {
        var userA = await CreateUserWithCopyAsync();
        var userB = await CreateUserWithCopyAsync();

        await using (var context = _fixture.CreateContext())
        {
            await new UserWriteRepository(context).DeleteWithAllDataAsync(userA);
        }

        await using (var context = _fixture.CreateContext())
        {
            Assert.False(await context.Set<Calendar>().AnyAsync(c => c.UserId == Id(userA)));
            Assert.False(await context.Set<CalendarEventResource>().AnyAsync(e => e.UserId == Id(userA)));
            Assert.True(await context.Set<CalendarEventResource>().AnyAsync(e => e.UserId == Id(userB)));
        }
    }

    [Fact]
    public async Task Deleting_a_calendar_deletes_only_its_events()
    {
        var user = await CreateUserWithCopyAsync();

        await using (var context = _fixture.CreateContext())
        {
            var work = await context.Set<Calendar>().SingleAsync(c => c.UserId == Id(user) && c.DisplayName == "Trabajo");
            context.Remove(work);
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            var left = await context.Set<CalendarEventResource>().Where(e => e.UserId == Id(user)).ToListAsync();
            Assert.Equal("cena.ics", Assert.Single(left).Href);
        }
    }

    private static UserId Id(Guid id) => UserId.CreateFromDatabase(id);

    /// <summary>Un usuario conectado con dos calendarios y un evento en cada uno.</summary>
    private async Task<Guid> CreateUserWithCopyAsync()
    {
        await using var context = _fixture.CreateContext();

        var user = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        context.Set<User>().Add(user);
        context.Set<CalendarConnection>().Add(CalendarConnection.Create(
            user.Id.Value, CalendarProvider.CalDav, "sergio", "https://dav.example.test/cal/", "cifrado", DateTime.UtcNow));

        var personal = Calendar.Create(user.Id, "https://dav.example.test/cal/personal/", "Personal", "#FF0000FF");
        var work = Calendar.Create(user.Id, "https://dav.example.test/cal/trabajo/", "Trabajo", null);
        context.Set<Calendar>().AddRange(personal, work);

        var fields = new CalendarEventFields("uid", "Evento", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), false, false);
        context.Set<CalendarEventResource>().AddRange(
            CalendarEventResource.Create(personal.Id, user.Id, "cena.ics", "\"1\"", "BEGIN:VCALENDAR\r\nEND:VCALENDAR", fields),
            CalendarEventResource.Create(work.Id, user.Id, "reunion.ics", "\"1\"", "BEGIN:VCALENDAR\r\nEND:VCALENDAR", fields));

        await context.SaveChangesAsync();
        return user.Id.Value;
    }
}
