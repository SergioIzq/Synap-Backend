using Synap.Application.Features.Calendar;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Las operaciones CalDAV que necesita la sincronización (calendar-store-and-view design.md
/// Decisión 3). Todas son de lectura: Synap nunca cambia nada en el servidor. Ninguna lanza por un
/// fallo del servidor; lo devuelven como <see cref="CalDavResult{T}"/>.
/// </summary>
public interface ICalDavSyncClient
{
    /// <summary>
    /// `PROPFIND Depth: 1` sobre el home: las colecciones que admiten VEVENT, con su nombre, color,
    /// `getctag` y si admiten `sync-collection`.
    /// </summary>
    Task<CalDavResult<IReadOnlyList<RemoteCalendar>>> ListCalendarsAsync(
        Uri home, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken);

    /// <summary>
    /// `REPORT sync-collection` (RFC 6578) desde <paramref name="syncToken"/>; sin token, lista todo.
    /// Un token que el servidor ya no reconoce vuelve como <see cref="CalDavResult{T}.TokenInvalid"/>.
    /// </summary>
    Task<CalDavResult<SyncCollectionChanges>> SyncCollectionAsync(
        Uri calendar, CalendarCredentials.CalDav credentials, string? syncToken, CancellationToken cancellationToken);

    /// <summary>`REPORT calendar-multiget` en lotes: el `.ics` y el `ETag` de cada `href` pedido.</summary>
    Task<CalDavResult<MultigetResult>> MultigetAsync(
        Uri calendar, CalendarCredentials.CalDav credentials, IReadOnlyList<string> hrefs, CancellationToken cancellationToken);

    /// <summary>`PROPFIND Depth: 1` de `getetag`: los recursos del calendario y su versión.</summary>
    Task<CalDavResult<IReadOnlyList<RemoteETag>>> ListETagsAsync(
        Uri calendar, CalendarCredentials.CalDav credentials, CancellationToken cancellationToken);
}

/// <summary>Lo que respondió el servidor, sin excepciones.</summary>
public abstract record CalDavResult<T>
{
    private CalDavResult()
    {
    }

    public sealed record Ok(T Value) : CalDavResult<T>;

    /// <summary>`401`/`403`: las credenciales ya no sirven.</summary>
    public sealed record Rejected : CalDavResult<T>;

    /// <summary>El servidor rechazó el punto de partida (`valid-sync-token`).</summary>
    public sealed record TokenInvalid : CalDavResult<T>;

    /// <summary>
    /// Fallo temporal. <paramref name="Category"/> es lo que se guarda (CalendarSyncErrors);
    /// <paramref name="Cause"/> es para los registros. Ninguno lleva contenido de la respuesta.
    /// </summary>
    public sealed record Failed(string Category, string Cause) : CalDavResult<T>;
}

/// <summary>
/// Una colección de eventos del home. <see cref="IsRequested"/> es que es la propia URL que se
/// listó: así se reconoce una conexión antigua que guardaba un calendario en lugar del home.
/// </summary>
public sealed record RemoteCalendar(
    string Href,
    string? DisplayName,
    string? Color,
    string? CTag,
    bool SupportsSyncCollection,
    bool IsRequested);

/// <summary>Un recurso y su versión. <see cref="Href"/> es la URL absoluta.</summary>
public sealed record RemoteETag(string Href, string? ETag);

/// <summary>
/// Lo que cambió desde el token: los recursos nuevos o cambiados con su `ETag`, los borrados
/// (`404`) y el token nuevo. <see cref="Truncated"/> es que el servidor cortó la lista (`507`): lo
/// que falta llegará en la siguiente sincronización.
/// </summary>
public sealed record SyncCollectionChanges(
    IReadOnlyList<RemoteETag> Changed,
    IReadOnlyList<string> Removed,
    string? NewToken,
    bool Truncated);

/// <summary>Un recurso descargado.</summary>
public sealed record RemoteResource(string Href, string? ETag, string Ics);

/// <summary>Los recursos descargados y los que el servidor ya no tiene (`404`).</summary>
public sealed record MultigetResult(IReadOnlyList<RemoteResource> Resources, IReadOnlyList<string> Missing);
