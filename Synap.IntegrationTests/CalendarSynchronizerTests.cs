using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Infrastructure.Services.Calendar;
using Synap.Infrastructure.Services.Secrets;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects;
using System.Security.Cryptography;
using Xunit;

namespace Synap.IntegrationTests;

/// <summary>
/// calendar-store-and-view tarea 1.4 contra un Postgres real: el sincronizador cruza calendarios,
/// sincronización incremental o por `ctag`/`ETag`, token caducado y preferencias conservadas
/// (specs/calendar-integration, design.md Decisión 3).
/// </summary>
[Collection(PostgresCollection.Name)]
public class CalendarSynchronizerTests
{
    private static readonly string Home = "https://dav.example.test/sergio/";
    private static readonly string Personal = "https://dav.example.test/sergio/personal/";
    private static readonly string Work = "https://dav.example.test/sergio/trabajo/";

    private static readonly string SimpleIcs =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" +
        "BEGIN:VEVENT\r\nUID:test-event\r\nDTSTAMP:20261001T120000Z\r\n" +
        "DTSTART:20261114T090000Z\r\nDTEND:20261114T100000Z\r\nSUMMARY:Reunión\r\n" +
        "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private static readonly string AnotherIcs =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" +
        "BEGIN:VEVENT\r\nUID:test-event\r\nDTSTAMP:20261001T130000Z\r\n" +
        "DTSTART:20261115T090000Z\r\nDTEND:20261115T100000Z\r\nSUMMARY:Reunión movida\r\n" +
        "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private readonly PostgresFixture _fixture;
    private readonly ISecretProtector _protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));

    public CalendarSynchronizerTests(PostgresFixture fixture) => _fixture = fixture;

    // ---- Alta, cambio y baja de evento ----

    [Fact]
    public async Task A_new_event_in_the_server_is_added_to_the_copy()
    {
        var userId = await CreateConnectionAsync();
        var client = SyncClient(
            home: [RemotePersonal()],
            syncs: new() { [Personal] = SyncResult(added: ["cena.ics"], token: "tok-1") },
            resources: new() { [Personal] = [Resource("cena.ics", "\"e1\"")] });

        await SyncAsync(userId, client);

        await using var context = _fixture.CreateContext();
        var @event = Assert.Single(await EventsOf(context, userId));
        Assert.Equal(Personal + "cena.ics", @event.Href);
        Assert.Equal("\"e1\"", @event.ETag);
    }

    [Fact]
    public async Task A_changed_event_replaces_its_copy()
    {
        var userId = await CreateConnectionAsync();
        await SeedEventAsync(userId, Personal, "cena.ics", "\"e1\"");

        var client = SyncClient(
            home: [RemotePersonal(ctag: "ctag-2")],
            syncs: new() { [Personal] = SyncResult(added: ["cena.ics"], token: "tok-2", etag: "\"e2\"") },
            resources: new() { [Personal] = [Resource("cena.ics", "\"e2\"", AnotherIcs)] });

        await SyncAsync(userId, client);

        await using var context = _fixture.CreateContext();
        var @event = Assert.Single(await EventsOf(context, userId));
        Assert.Equal("\"e2\"", @event.ETag);
    }

    [Fact]
    public async Task A_removed_event_is_deleted_from_the_copy()
    {
        var userId = await CreateConnectionAsync();
        await SeedEventAsync(userId, Personal, "cena.ics", "\"e1\"");

        var client = SyncClient(
            home: [RemotePersonal(ctag: "ctag-2")],
            syncs: new() { [Personal] = SyncResult(removed: ["cena.ics"], token: "tok-2") },
            resources: new());

        await SyncAsync(userId, client);

        await using var context = _fixture.CreateContext();
        Assert.Empty(await EventsOf(context, userId));
    }

    // ---- Calendario nuevo y borrado ----

    [Fact]
    public async Task A_new_calendar_in_the_server_appears_in_the_copy()
    {
        var userId = await CreateConnectionAsync();

        var client = SyncClient(
            home: [RemotePersonal(), RemoteWork()],
            syncs: new()
            {
                [Personal] = SyncResult(token: "tok-1"),
                [Work] = SyncResult(token: "tok-w"),
            },
            resources: new());

        await SyncAsync(userId, client);

        await using var context = _fixture.CreateContext();
        var calendars = await CalendarsOf(context, userId);
        Assert.Equal(2, calendars.Count);
        Assert.Contains(calendars, c => c.Href == Personal);
        Assert.Contains(calendars, c => c.Href == Work);
        // Los nuevos empiezan activos y Personal (specs/calendar-integration).
        Assert.All(calendars, c => Assert.True(c.Enabled));
        Assert.All(calendars, c => Assert.Equal(CalendarScope.Personal, c.Scope));
    }

    [Fact]
    public async Task A_calendar_removed_from_the_server_is_deleted_with_its_events()
    {
        var userId = await CreateConnectionAsync();
        await SeedEventAsync(userId, Work, "reunion.ics", "\"e1\"");

        // El servidor solo devuelve Personal; Trabajo ya no aparece.
        var client = SyncClient(
            home: [RemotePersonal()],
            syncs: new() { [Personal] = SyncResult(token: "tok-1") },
            resources: new());

        await SyncAsync(userId, client);

        await using var context = _fixture.CreateContext();
        var calendars = await CalendarsOf(context, userId);
        Assert.Single(calendars);
        Assert.Equal(Personal, calendars[0].Href);
        Assert.Empty(await EventsOf(context, userId));
    }

    // ---- Token caducado que rehace la copia ----

    [Fact]
    public async Task An_expired_token_triggers_a_full_copy_rebuild()
    {
        var userId = await CreateConnectionAsync();
        await SeedEventAsync(userId, Personal, "viejo.ics", "\"ev1\"");

        var callCount = 0;
        var client = new SyncClientStub(
            listCalendars: _ => new CalDavResult<IReadOnlyList<RemoteCalendar>>.Ok([RemotePersonal()]),
            syncCollection: (_, _, token) =>
            {
                callCount++;
                // Primera llamada con token guardado → token inválido.
                // Segunda llamada sin token → sincronización completa.
                if (callCount == 1)
                {
                    return new CalDavResult<SyncCollectionChanges>.TokenInvalid();
                }

                return new CalDavResult<SyncCollectionChanges>.Ok(
                    new SyncCollectionChanges(
                        [new RemoteETag(Personal + "nuevo.ics", "\"en1\"")], [], "tok-nuevo", false));
            },
            multiget: (_, _, _) => new CalDavResult<MultigetResult>.Ok(
                new MultigetResult([new RemoteResource(Personal + "nuevo.ics", "\"en1\"", SimpleIcs)], [])),
            listEtags: _ => new CalDavResult<IReadOnlyList<RemoteETag>>.Ok([]));

        // Sembramos un token guardado para que la primera llamada lo use.
        await SetSyncTokenAsync(userId, Personal, "tok-caducado");

        await SyncAsync(userId, client);

        await using var context = _fixture.CreateContext();
        var events = await EventsOf(context, userId);
        // `viejo.ics` desapareció del servidor; `nuevo.ics` tomó su lugar.
        var @event = Assert.Single(events);
        Assert.Equal(Personal + "nuevo.ics", @event.Href);
        // Dos llamadas a SyncCollection: la primera con el token caducado, la segunda sin token.
        Assert.Equal(2, callCount);
    }

    // ---- Preferencias conservadas ----

    [Fact]
    public async Task User_preferences_are_kept_across_syncs()
    {
        var userId = await CreateConnectionAsync();

        // Primera sincronización: se crean los calendarios.
        var client = SyncClient(
            home: [RemotePersonal(), RemoteWork()],
            syncs: new() { [Personal] = SyncResult(token: "t1"), [Work] = SyncResult(token: "tw") },
            resources: new());

        await SyncAsync(userId, client);

        // El usuario cambia sus preferencias en Synap.
        await using (var ctx = _fixture.CreateContext())
        {
            var personal = await ctx.Set<Domain.Calendar>().SingleAsync(c => c.UserId.Value == userId && c.Href == Personal);
            var work = await ctx.Set<Domain.Calendar>().SingleAsync(c => c.UserId.Value == userId && c.Href == Work);

            personal.ChangeScope(CalendarScope.Work);
            var colorResult = personal.ChangeColor("#123456");
            Assert.True(colorResult.IsSuccess);
            work.Disable();

            await ctx.SaveChangesAsync();
        }

        // Segunda sincronización: los mismos calendarios.
        await SyncAsync(userId, client);

        // Las preferencias se conservan.
        await using (var ctx = _fixture.CreateContext())
        {
            var personal = await ctx.Set<Domain.Calendar>().SingleAsync(c => c.UserId.Value == userId && c.Href == Personal);
            var work = await ctx.Set<Domain.Calendar>().SingleAsync(c => c.UserId.Value == userId && c.Href == Work);

            Assert.Equal(CalendarScope.Work, personal.Scope);
            Assert.Equal("#123456", personal.Color);
            Assert.False(work.Enabled);
        }
    }

    // ---- Concurrencia (tarea 1.5) ----

    /// <summary>
    /// El lock evita que dos sincronizaciones del mismo usuario se pisen: la segunda obtiene
    /// `AlreadyRunning` sin esperar (design.md Decisión 4).
    /// </summary>
    [Fact]
    public async Task Two_concurrent_syncs_of_the_same_user_do_not_overlap()
    {
        var userId = Guid.NewGuid();
        var @lock = new PostgresCalendarSyncLock(_fixture.ConnectionString);

        // Primera adquisición: tiene el lock.
        await using var held = await @lock.TryAcquireAsync(userId, CancellationToken.None);
        Assert.NotNull(held);

        // Segunda: el mismo usuario ya tiene el lock → no puede adquirirlo.
        var second = await @lock.TryAcquireAsync(userId, CancellationToken.None);
        Assert.Null(second);
    }

    /// <summary>
    /// Dos usuarios con locks distintos nunca se bloquean entre sí: la xact_lock es por usuario
    /// (design.md Decisión 4).
    /// </summary>
    [Fact]
    public async Task A_hanging_server_does_not_block_another_user()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var @lock = new PostgresCalendarSyncLock(_fixture.ConnectionString);

        // A adquiere su lock.
        await using var heldA = await @lock.TryAcquireAsync(userA, CancellationToken.None);
        Assert.NotNull(heldA);

        // B puede adquirir el suyo aunque A siga activo: su lock tiene otro key.
        await using var heldB = await @lock.TryAcquireAsync(userB, CancellationToken.None);
        Assert.NotNull(heldB);

        // Mientras A sigue bloqueado, B también puede volver a fallar si intenta otro:
        var secondB = await @lock.TryAcquireAsync(userB, CancellationToken.None);
        Assert.Null(secondB);
    }

    // ---- Helpers ----

    private static RemoteCalendar RemotePersonal(string ctag = "ctag-1") =>
        new(Personal, "Personal", "#FF0000", ctag, true, false);

    private static RemoteCalendar RemoteWork(string ctag = "ctag-w") =>
        new(Work, "Trabajo", null, ctag, true, false);

    private static SyncCollectionChanges SyncResult(
        IEnumerable<string>? added = null,
        IEnumerable<string>? removed = null,
        string token = "tok",
        string etag = "\"e1\"")
    {
        var changed = (added ?? []).Select(name => new RemoteETag(Personal + name, etag)).ToList();
        var removedList = (removed ?? []).Select(name => Personal + name).ToList();
        return new SyncCollectionChanges(changed, removedList, token, false);
    }

    private static RemoteResource Resource(string name, string etag, string? ics = null) =>
        new(Personal + name, etag, ics ?? SimpleIcs);

    private ICalDavSyncClient SyncClient(
        IReadOnlyList<RemoteCalendar> home,
        Dictionary<string, SyncCollectionChanges> syncs,
        Dictionary<string, List<RemoteResource>> resources)
    {
        return new SyncClientStub(
            listCalendars: _ => new CalDavResult<IReadOnlyList<RemoteCalendar>>.Ok(home),
            syncCollection: (calendarUrl, _, _) =>
            {
                var url = calendarUrl.AbsoluteUri;
                var changes = syncs.TryGetValue(url, out var c)
                    ? c
                    : new SyncCollectionChanges([], [], null, false);
                return new CalDavResult<SyncCollectionChanges>.Ok(changes);
            },
            multiget: (calendarUrl, _, hrefs) =>
            {
                var url = calendarUrl.AbsoluteUri;
                var available = resources.TryGetValue(url, out var r) ? r : [];
                var matching = available.Where(res => hrefs.Contains(res.Href)).ToList();
                return new CalDavResult<MultigetResult>.Ok(new MultigetResult(matching, []));
            },
            listEtags: _ => new CalDavResult<IReadOnlyList<RemoteETag>>.Ok([]));
    }

    private async Task<Guid> CreateConnectionAsync()
    {
        await using var context = _fixture.CreateContext();

        var user = User.Create(
            Email.CreateFromDatabase($"{Guid.NewGuid():N}@example.com"),
            PasswordHash.CreateFromDatabase("hash"));
        context.Set<User>().Add(user);

        var creds = new CalendarCredentialsProtector(_protector)
            .Protect(new CalendarCredentials.CalDav("test@example.com", "app-pass"));

        context.Set<CalendarConnection>().Add(
            CalendarConnection.Create(user.Id.Value, CalendarProvider.CalDav, "test@example.com", Home, creds, DateTime.UtcNow));

        await context.SaveChangesAsync();
        return user.Id.Value;
    }

    private async Task SeedEventAsync(Guid userId, string calendarHref, string eventName, string etag, string? ics = null)
    {
        await using var context = _fixture.CreateContext();

        var uid = UserIdOf(userId);

        var calendar = await context.Set<Domain.Calendar>()
            .AsTracking()
            .FirstOrDefaultAsync(c => c.UserId == uid && c.Href == calendarHref);

        if (calendar is null)
        {
            calendar = Domain.Calendar.Create(uid, calendarHref, calendarHref.Split('/').Last(s => !string.IsNullOrEmpty(s)), null);
            context.Set<Domain.Calendar>().Add(calendar);
            await context.SaveChangesAsync();
        }

        var fields = new CalendarEventFields(
            Uid: "test-uid",
            Summary: "Test",
            StartsAtUtc: new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc),
            EndsAtUtc: new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc),
            IsRecurring: false,
            AllDay: false);

        context.Set<CalendarEventResource>().Add(
            CalendarEventResource.Create(calendar.Id, uid, calendarHref + eventName, etag, ics ?? SimpleIcs, fields));

        await context.SaveChangesAsync();
    }

    private async Task SetSyncTokenAsync(Guid userId, string calendarHref, string token)
    {
        await using var context = _fixture.CreateContext();
        var uid = UserIdOf(userId);
        var calendar = await context.Set<Domain.Calendar>()
            .AsTracking()
            .FirstAsync(c => c.UserId == uid && c.Href == calendarHref);
        calendar.RecordSynced(token, null, DateTime.UtcNow);
        await context.SaveChangesAsync();
    }

    private static Task<List<CalendarEventResource>> EventsOf(SynapDbContext ctx, Guid userId) =>
        ctx.Set<CalendarEventResource>()
            .AsNoTracking()
            .Where(e => e.UserId.Value == userId)
            .ToListAsync();

    private static Task<List<Domain.Calendar>> CalendarsOf(SynapDbContext ctx, Guid userId) =>
        ctx.Set<Domain.Calendar>()
            .AsNoTracking()
            .Where(c => c.UserId.Value == userId)
            .ToListAsync();

    private async Task SyncAsync(Guid userId, ICalDavSyncClient client)
    {
        await using var context = _fixture.CreateContext();
        var synchronizer = new CalendarSynchronizer(
            context,
            new NoOpSyncLock(),
            client,
            new NoOpDiscovery(),
            new CalendarCredentialsProtector(_protector),
            NullLogger<CalendarSynchronizer>.Instance);

        await synchronizer.SyncAsync(userId);
    }

    private static Synap.Shared.Domain.ValueObjects.Ids.UserId UserIdOf(Guid id) =>
        Synap.Shared.Domain.ValueObjects.Ids.UserId.CreateFromDatabase(id);

    // ---- Stubs ----

    /// <summary>Concede el lock siempre; no usa Postgres.</summary>
    private sealed class NoOpSyncLock : ICalendarSyncLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable?>(new NoOpDisposable());

        private sealed class NoOpDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Devuelve `Found` para cualquier URL; no hace HTTP.</summary>
    private sealed class NoOpDiscovery : ICalDavDiscovery
    {
        public Task<CalDavDiscoveryResult> DiscoverAsync(
            Uri url, string username, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult<CalDavDiscoveryResult>(new CalDavDiscoveryResult.Found(url.AbsoluteUri));
    }

    /// <summary>CalDAV configurable con lambdas para cada operación.</summary>
    private sealed class SyncClientStub(
        Func<Uri, CalDavResult<IReadOnlyList<RemoteCalendar>>> listCalendars,
        Func<Uri, CalendarCredentials.CalDav, string?, CalDavResult<SyncCollectionChanges>> syncCollection,
        Func<Uri, CalendarCredentials.CalDav, IReadOnlyList<string>, CalDavResult<MultigetResult>> multiget,
        Func<Uri, CalDavResult<IReadOnlyList<RemoteETag>>> listEtags)
        : ICalDavSyncClient
    {
        public Task<CalDavResult<IReadOnlyList<RemoteCalendar>>> ListCalendarsAsync(
            Uri home, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken) =>
            Task.FromResult(listCalendars(home));

        public Task<CalDavResult<SyncCollectionChanges>> SyncCollectionAsync(
            Uri calendar, CalendarCredentials.CalDav credentials, string? syncToken, CancellationToken cancellationToken) =>
            Task.FromResult(syncCollection(calendar, credentials, syncToken));

        public Task<CalDavResult<MultigetResult>> MultigetAsync(
            Uri calendar, CalendarCredentials.CalDav credentials, IReadOnlyList<string> hrefs, CancellationToken cancellationToken) =>
            Task.FromResult(multiget(calendar, credentials, hrefs));

        public Task<CalDavResult<IReadOnlyList<RemoteETag>>> ListETagsAsync(
            Uri calendar, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken) =>
            Task.FromResult(listEtags(calendar));
    }
}
