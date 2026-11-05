using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;
using StoredCalendar = Synap.Domain.Calendar;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// La sincronización de un usuario (calendar-store-and-view design.md Decisión 3):
/// 1. lista los calendarios del home y los cruza con los guardados, conservando las preferencias;
/// 2. trae de cada calendario activo lo que cambió, por `sync-collection` o por `ctag`/`ETag`;
/// 3. escribe cada calendario en una sola transacción (un único SaveChanges), con su hora.
///
/// Primero se pregunta al servidor y después se escribe: un fallo a medias deja la copia de ese
/// calendario como estaba. Un `401` marca la conexión para reconectar; un fallo de red o un `5xx`
/// se anota como categoría en el calendario.
///
/// Los registros llevan ids, `href` del calendario y categorías; nunca un título ni un `.ics`.
/// </summary>
public sealed class CalendarSynchronizer : ICalendarSynchronizer
{
    private readonly SynapDbContext _context;
    private readonly ICalendarSyncLock _lock;
    private readonly ICalDavSyncClient _client;
    private readonly ICalDavDiscovery _discovery;
    private readonly CalendarCredentialsProtector _credentialsProtector;
    private readonly ILogger<CalendarSynchronizer> _logger;

    public CalendarSynchronizer(
        SynapDbContext context,
        ICalendarSyncLock syncLock,
        ICalDavSyncClient client,
        ICalDavDiscovery discovery,
        CalendarCredentialsProtector credentialsProtector,
        ILogger<CalendarSynchronizer> logger)
    {
        _context = context;
        _lock = syncLock;
        _client = client;
        _discovery = discovery;
        _credentialsProtector = credentialsProtector;
        _logger = logger;
    }

    public async Task<CalendarSyncOutcome> SyncAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var held = await _lock.TryAcquireAsync(userId, cancellationToken);
        if (held is null)
        {
            return CalendarSyncOutcome.AlreadyRunning;
        }

        var user = UserId.CreateFromDatabase(userId);
        var connection = await _context.Set<CalendarConnection>()
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == user, cancellationToken);

        if (connection is null)
        {
            return CalendarSyncOutcome.NotConnected;
        }

        // Solo la quita reconectar (specs/calendar-integration "Reconexión").
        if (connection.NeedsReconnect)
        {
            return CalendarSyncOutcome.NeedsReconnect;
        }

        if (_credentialsProtector.TryRead(connection.Provider, connection.CredentialsEncrypted) is not CalendarCredentials.CalDav credentials
            || !Uri.TryCreate(connection.CalendarRef, UriKind.Absolute, out var home))
        {
            return await MarkNeedsReconnectAsync(connection, "credenciales ilegibles", cancellationToken);
        }

        var listing = await ListHomeAsync(connection, home, credentials, cancellationToken);
        switch (listing)
        {
            case CalDavResult<IReadOnlyList<RemoteCalendar>>.Rejected:
                return await MarkNeedsReconnectAsync(connection, "acceso rechazado", cancellationToken);
            case CalDavResult<IReadOnlyList<RemoteCalendar>>.Failed failed:
                return await RecordHomeFailedAsync(user, failed, cancellationToken);
            case not CalDavResult<IReadOnlyList<RemoteCalendar>>.Ok:
                return await RecordHomeFailedAsync(
                    user, new CalDavResult<IReadOnlyList<RemoteCalendar>>.Failed(CalendarSyncErrors.InvalidResponse, "respuesta inesperada"),
                    cancellationToken);
        }

        var remote = ((CalDavResult<IReadOnlyList<RemoteCalendar>>.Ok)listing).Value;
        var calendars = await ReconcileCalendarsAsync(user, remote, cancellationToken);

        var outcome = CalendarSyncOutcome.Synced;
        foreach (var (calendar, remoteCalendar) in calendars.Where(c => c.Stored.Enabled))
        {
            var result = await SyncCalendarAsync(calendar, remoteCalendar, credentials, cancellationToken);
            if (result == CalendarSyncOutcome.NeedsReconnect)
            {
                return await MarkNeedsReconnectAsync(connection, "acceso rechazado", cancellationToken);
            }

            if (result == CalendarSyncOutcome.Failed)
            {
                outcome = CalendarSyncOutcome.Failed;
            }
        }

        return outcome;
    }

    /// <summary>
    /// Lista el home. Si lo guardado resulta ser un calendario (una conexión de antes de que
    /// existiera la copia), se vuelve a descubrir el home con las credenciales guardadas.
    /// </summary>
    private async Task<CalDavResult<IReadOnlyList<RemoteCalendar>>> ListHomeAsync(
        CalendarConnection connection, Uri home, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken)
    {
        var listing = await _client.ListCalendarsAsync(home, credentials, cancellationToken);
        if (listing is not CalDavResult<IReadOnlyList<RemoteCalendar>>.Ok ok || !ok.Value.Any(c => c.IsRequested))
        {
            return listing;
        }

        var discovered = await _discovery.DiscoverAsync(home, credentials.Username, credentials.Password, cancellationToken);
        if (discovered is CalDavDiscoveryResult.CredentialsRejected)
        {
            return new CalDavResult<IReadOnlyList<RemoteCalendar>>.Rejected();
        }

        if (discovered is not CalDavDiscoveryResult.Found found
            || !Uri.TryCreate(found.HomeUrl, UriKind.Absolute, out var newHome)
            || newHome.AbsoluteUri == home.AbsoluteUri)
        {
            // El servidor no deja llegar más arriba: el calendario sigue haciendo de home.
            return listing;
        }

        var fromHome = await _client.ListCalendarsAsync(newHome, credentials, cancellationToken);
        if (fromHome is CalDavResult<IReadOnlyList<RemoteCalendar>>.Ok)
        {
            _logger.LogInformation("Conexión de calendario de {UserId} apuntada a su home", connection.UserId.Value);
            connection.PointTo(newHome.AbsoluteUri);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return fromHome;
    }

    /// <summary>
    /// Los nuevos entran activos y Personal, los que ya no están se borran con sus eventos, y en
    /// los demás solo cambian el nombre y el color del servidor.
    /// </summary>
    private async Task<List<(StoredCalendar Stored, RemoteCalendar Remote)>> ReconcileCalendarsAsync(
        UserId user, IReadOnlyList<RemoteCalendar> remote, CancellationToken cancellationToken)
    {
        var stored = await _context.Set<StoredCalendar>()
            .AsTracking()
            .Where(c => c.UserId == user)
            .ToListAsync(cancellationToken);

        var byHref = stored.ToDictionary(c => c.Href, StringComparer.Ordinal);
        var result = new List<(StoredCalendar, RemoteCalendar)>();

        foreach (var calendar in remote.DistinctBy(c => c.Href))
        {
            if (byHref.Remove(calendar.Href, out var existing))
            {
                existing.UpdateFromServer(calendar.DisplayName, calendar.Color);
                result.Add((existing, calendar));
                continue;
            }

            var created = StoredCalendar.Create(user, calendar.Href, calendar.DisplayName, calendar.Color);
            _context.Add(created);
            result.Add((created, calendar));
        }

        // Lo que queda ya no está en el servidor; sus eventos se van por la cascada.
        _context.RemoveRange(byHref.Values);
        await _context.SaveChangesAsync(cancellationToken);

        return result;
    }

    private async Task<CalendarSyncOutcome> SyncCalendarAsync(
        StoredCalendar calendar, RemoteCalendar remote, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken)
    {
        var url = new Uri(calendar.Href);
        var stored = await _context.Set<CalendarEventResource>()
            .AsNoTracking()
            .Where(e => e.CalendarId == calendar.Id)
            .Select(e => new { e.Href, e.ETag })
            .ToDictionaryAsync(e => e.Href, e => e.ETag, StringComparer.Ordinal, cancellationToken);

        var plan = remote.SupportsSyncCollection
            ? await PlanBySyncCollectionAsync(calendar, url, credentials, stored, cancellationToken)
            : await PlanByETagsAsync(calendar, remote, url, credentials, stored, cancellationToken);

        if (plan.Failure is { } planFailure)
        {
            return await RecordCalendarFailedAsync(calendar, planFailure, cancellationToken);
        }

        var downloaded = new List<RemoteResource>();
        var removed = new HashSet<string>(plan.Removed, StringComparer.Ordinal);

        if (plan.Download.Count > 0)
        {
            var multiget = await _client.MultigetAsync(url, credentials, plan.Download, cancellationToken);
            if (ToFailure(multiget) is { } multigetFailure)
            {
                return await RecordCalendarFailedAsync(calendar, multigetFailure, cancellationToken);
            }

            var result = ((CalDavResult<MultigetResult>.Ok)multiget).Value;
            downloaded.AddRange(result.Resources);
            removed.UnionWith(result.Missing);
        }

        await ApplyAsync(calendar, downloaded, removed, plan.NewToken, remote.CTag, cancellationToken);
        return CalendarSyncOutcome.Synced;
    }

    private sealed record SyncPlan(
        IReadOnlyList<string> Download,
        IReadOnlyList<string> Removed,
        string? NewToken,
        Failure? Failure = null);

    private sealed record Failure(bool Rejected, string Category, string Cause);

    /// <summary>
    /// RFC 6578: lo que cambió desde el token. Sin token (la primera vez, o porque el servidor
    /// rechazó el anterior) la lista es completa, y lo que falta en ella se borra de la copia.
    /// </summary>
    private async Task<SyncPlan> PlanBySyncCollectionAsync(
        StoredCalendar calendar, Uri url, CalendarCredentials.CalDav credentials,
        IReadOnlyDictionary<string, string?> stored, CancellationToken cancellationToken)
    {
        var token = calendar.SyncToken;
        var answer = await _client.SyncCollectionAsync(url, credentials, token, cancellationToken);

        if (answer is CalDavResult<SyncCollectionChanges>.TokenInvalid && token is not null)
        {
            _logger.LogInformation("Token de sincronización rechazado para el calendario {CalendarId}; se rehace la copia", calendar.Id.Value);
            token = null;
            answer = await _client.SyncCollectionAsync(url, credentials, null, cancellationToken);
        }

        if (ToFailure(answer) is { } failure)
        {
            return new SyncPlan([], [], null, failure);
        }

        var changes = ((CalDavResult<SyncCollectionChanges>.Ok)answer).Value;
        var download = changes.Changed
            .Where(c => !stored.TryGetValue(c.Href, out var etag) || etag is null || etag != c.ETag)
            .Select(c => c.Href)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var removed = changes.Removed.ToList();
        if (token is null && !changes.Truncated)
        {
            var listed = changes.Changed.Select(c => c.Href).ToHashSet(StringComparer.Ordinal);
            removed.AddRange(stored.Keys.Where(href => !listed.Contains(href)));
        }

        return new SyncPlan(download, removed, changes.NewToken);
    }

    /// <summary>
    /// Sin `sync-collection`: si el `ctag` no cambió no hay nada que hacer; si cambió, se comparan
    /// los `ETag` del servidor con los de la copia.
    /// </summary>
    private async Task<SyncPlan> PlanByETagsAsync(
        StoredCalendar calendar, RemoteCalendar remote, Uri url, CalendarCredentials.CalDav credentials,
        IReadOnlyDictionary<string, string?> stored, CancellationToken cancellationToken)
    {
        if (remote.CTag is not null && remote.CTag == calendar.CTag && calendar.LastSyncedAt is not null)
        {
            return new SyncPlan([], [], null);
        }

        var answer = await _client.ListETagsAsync(url, credentials, cancellationToken);
        if (ToFailure(answer) is { } failure)
        {
            return new SyncPlan([], [], null, failure);
        }

        var etags = ((CalDavResult<IReadOnlyList<RemoteETag>>.Ok)answer).Value;
        var listed = etags.Select(e => e.Href).ToHashSet(StringComparer.Ordinal);

        var download = etags
            .Where(e => !stored.TryGetValue(e.Href, out var etag) || etag is null || etag != e.ETag)
            .Select(e => e.Href)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new SyncPlan(download, stored.Keys.Where(href => !listed.Contains(href)).ToList(), null);
    }

    /// <summary>
    /// Todo lo de un calendario en un único SaveChanges, que es una transacción: o queda la versión
    /// nueva entera, con su token y su hora, o la anterior.
    /// </summary>
    private async Task ApplyAsync(
        StoredCalendar calendar, IReadOnlyList<RemoteResource> downloaded, IReadOnlySet<string> removed,
        string? newToken, string? ctag, CancellationToken cancellationToken)
    {
        var touched = downloaded.Select(r => r.Href).Concat(removed).Distinct(StringComparer.Ordinal).ToList();
        var existing = touched.Count == 0
            ? []
            : await _context.Set<CalendarEventResource>()
                .AsTracking()
                .Where(e => e.CalendarId == calendar.Id && touched.Contains(e.Href))
                .ToListAsync(cancellationToken);
        var byHref = existing.ToDictionary(e => e.Href, StringComparer.Ordinal);

        var unreadable = 0;
        foreach (var resource in downloaded.DistinctBy(r => r.Href))
        {
            if (removed.Contains(resource.Href))
            {
                continue;
            }

            var fields = IcsFields.TryRead(resource.Ics);
            if (fields is null)
            {
                // El servidor prevalece: una versión que no se puede leer tampoco deja la anterior.
                unreadable++;
                if (byHref.Remove(resource.Href, out var stale))
                {
                    _context.Remove(stale);
                }

                continue;
            }

            if (byHref.Remove(resource.Href, out var current))
            {
                current.Replace(resource.ETag, resource.Ics, fields);
            }
            else
            {
                _context.Add(CalendarEventResource.Create(calendar.Id, calendar.UserId, resource.Href, resource.ETag, resource.Ics, fields));
            }
        }

        foreach (var href in removed)
        {
            if (byHref.Remove(href, out var gone))
            {
                _context.Remove(gone);
            }
        }

        calendar.RecordSynced(newToken ?? calendar.SyncToken, ctag, DateTime.UtcNow);
        await _context.SaveChangesAsync(cancellationToken);

        if (unreadable > 0)
        {
            _logger.LogWarning("{Count} eventos del calendario {CalendarId} no se pudieron leer y se omitieron", unreadable, calendar.Id.Value);
        }
    }

    private static Failure? ToFailure<T>(CalDavResult<T> result) => result switch
    {
        CalDavResult<T>.Ok => null,
        CalDavResult<T>.Rejected => new Failure(true, string.Empty, "acceso rechazado"),
        CalDavResult<T>.Failed failed => new Failure(false, failed.Category, failed.Cause),
        _ => new Failure(false, CalendarSyncErrors.InvalidResponse, "respuesta inesperada"),
    };

    private async Task<CalendarSyncOutcome> RecordCalendarFailedAsync(
        StoredCalendar calendar, Failure failure, CancellationToken cancellationToken)
    {
        if (failure.Rejected)
        {
            return CalendarSyncOutcome.NeedsReconnect;
        }

        _logger.LogWarning(
            "No se pudo sincronizar el calendario {CalendarId} de {UserId}: {Cause}",
            calendar.Id.Value, calendar.UserId.Value, failure.Cause);

        calendar.RecordSyncFailed(failure.Category);
        await _context.SaveChangesAsync(cancellationToken);
        return CalendarSyncOutcome.Failed;
    }

    /// <summary>El home no respondió: todos los calendarios quedan anotados y la copia, intacta.</summary>
    private async Task<CalendarSyncOutcome> RecordHomeFailedAsync(
        UserId user, CalDavResult<IReadOnlyList<RemoteCalendar>>.Failed failed, CancellationToken cancellationToken)
    {
        _logger.LogWarning("No se pudo sincronizar el calendario de {UserId}: {Cause}", user.Value, failed.Cause);

        var calendars = await _context.Set<StoredCalendar>().AsTracking().Where(c => c.UserId == user).ToListAsync(cancellationToken);
        foreach (var calendar in calendars)
        {
            calendar.RecordSyncFailed(failed.Category);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return CalendarSyncOutcome.Failed;
    }

    private async Task<CalendarSyncOutcome> MarkNeedsReconnectAsync(
        CalendarConnection connection, string cause, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Calendario de {UserId} marcado para reconectar: {Cause}", connection.UserId.Value, cause);

        connection.MarkNeedsReconnect(DateTime.UtcNow);
        await _context.SaveChangesAsync(cancellationToken);
        return CalendarSyncOutcome.NeedsReconnect;
    }
}
