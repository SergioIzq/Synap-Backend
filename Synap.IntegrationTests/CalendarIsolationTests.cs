using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Infrastructure.Persistence.Data.Calendar;
using Synap.Infrastructure.Persistence.Data.Users;
using Synap.Infrastructure.Services.Calendar;
using Synap.Infrastructure.Services.Secrets;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;
using System.Net;
using System.Security.Cryptography;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// calendar-integration tareas 1.2 y 1.3 contra un Postgres real: la tabla `calendar_connections`
/// se borra con el usuario, las credenciales se guardan cifradas, y leer para un usuario nunca usa
/// la conexión de otro (specs/calendar-integration "Credenciales aisladas entre usuarios").
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
    public async Task One_users_calendar_is_never_read_for_another()
    {
        var (userA, userB) = await CreateUsersAsync();
        var calDav = new RecordingCalDavHandler();
        await ConnectAsync(userA);

        await using var context = _fixture.CreateContext();

        // Guardadas y cifradas: ni la contraseña ni el usuario están en la columna.
        var stored = await context.Set<CalendarConnection>().AsNoTracking().SingleAsync(c => c.Id == UserIdOf(userA));
        Assert.DoesNotContain(UserAPassword, stored.CredentialsEncrypted);
        Assert.Equal(UserACalendar, stored.CalendarRef);

        var reader = Reader(context, calDav);

        Assert.IsType<CalendarReadResult.NotConnected>(await reader.ListEventsAsync(userB, DayStartUtc, DayEndUtc, 10));
        Assert.IsType<CalendarReadResult.NotConnected>(await reader.ListEventsAsync(Guid.NewGuid(), DayStartUtc, DayEndUtc, 10));
        Assert.Empty(calDav.Requested);

        // Y el aislamiento no es vacío: la lectura de A sí va al servidor de A.
        Assert.IsType<CalendarReadResult.Ok>(await reader.ListEventsAsync(userA, DayStartUtc, DayEndUtc, 10));
        Assert.Equal([UserACalendar], calDav.Requested);
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

    private CalendarReader Reader(SynapDbContext context, RecordingCalDavHandler calDav) =>
        new(
            new CalendarConnectionWriteRepository(context),
            new ContextUnitOfWork(context),
            new CalendarCredentialsProtector(_protector),
            [new CalDavCalendarClient(new HttpClient(calDav), NullLogger<CalDavCalendarClient>.Instance)],
            NullLogger<CalendarReader>.Instance);

    private async Task<(Guid UserA, Guid UserB)> CreateUsersAsync()
    {
        await using var context = _fixture.CreateContext();

        var userA = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        var userB = User.Create(Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"), PasswordHash.CreateFromDatabase("hash"));
        context.Set<User>().AddRange(userA, userB);
        await context.SaveChangesAsync();

        return (userA.Id.Value, userB.Id.Value);
    }

    /// <summary>
    /// Answers an empty day, and records whose server was asked - which is how a read for the
    /// wrong user is caught even when its result would look innocent.
    /// </summary>
    private sealed class RecordingCalDavHandler : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent("""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" />"""),
            });
        }
    }

    private sealed class ContextUnitOfWork(SynapDbContext context) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);

        public Task CommitTransactionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RollbackTransactionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
