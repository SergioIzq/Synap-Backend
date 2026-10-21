using Microsoft.EntityFrameworkCore;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Data.Calendar;
using Synap.Infrastructure.Persistence.Data.Users;
using Synap.Shared.Domain.ValueObjects;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>calendar-integration tarea 1.2: la tabla calendar_connections contra un Postgres real.</summary>
[Collection(PostgresCollection.Name)]
public class CalendarConnectionPersistenceTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public CalendarConnectionPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    private static User NewUser()
        => User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));

    [Fact]
    public async Task Una_conexion_se_guarda_y_se_lee_por_el_usuario()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        await context.SaveChangesAsync();

        var connections = new CalendarConnectionRepository(context);
        await connections.CreateAsync(
            CalendarConnection.Create(user.Id, CalendarProvider.CalDav, "ana", "https://dav.example.com/cal/", "cifrado", Now), default);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await connections.GetByIdAsync(user.Id.Value, default);

        Assert.NotNull(stored);
        Assert.Equal(CalendarProvider.CalDav, stored.Provider);
        Assert.Equal("https://dav.example.com/cal/", stored.CalendarRef);
        Assert.False(stored.NeedsReconnect);
    }

    [Fact]
    public async Task Borrar_la_cuenta_borra_su_conexion()
    {
        await using var context = _fixture.CreateContext();
        var user = NewUser();
        context.Add(user);
        context.Add(CalendarConnection.Create(user.Id, CalendarProvider.Google, "ana@example.com", "primary", "cifrado", Now));
        await context.SaveChangesAsync();

        await new UserWriteRepository(context).DeleteWithAllDataAsync(user.Id.Value);

        Assert.False(await context.Set<CalendarConnection>().AnyAsync(c => c.Id == user.Id));
    }
}
