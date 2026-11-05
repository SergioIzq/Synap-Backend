using Synap.Shared.Application.BackgroundJobs;
using Microsoft.Extensions.DependencyInjection;

namespace Synap.Application.Features.Calendar;

/// <summary>
/// Trae a la copia de Synap lo que cambió en el servidor CalDAV de un usuario
/// (calendar-store-and-view design.md Decisiones 3 y 4). Nunca lanza por un servidor que falla:
/// lo devuelve en <see cref="CalendarSyncOutcome"/> y lo deja anotado en cada calendario.
/// </summary>
public interface ICalendarSynchronizer
{
    /// <summary>
    /// Sincroniza todos los calendarios activos del usuario. Si ya hay una sincronización suya en
    /// curso no espera: devuelve <see cref="CalendarSyncOutcome.AlreadyRunning"/>.
    /// </summary>
    Task<CalendarSyncOutcome> SyncAsync(Guid userId, CancellationToken cancellationToken = default);
}

public enum CalendarSyncOutcome
{
    NotConnected,
    AlreadyRunning,
    NeedsReconnect,

    /// <summary>Todos los calendarios activos se sincronizaron.</summary>
    Synced,

    /// <summary>El servidor no respondió, o falló algún calendario; la copia sigue como estaba.</summary>
    Failed,
}

/// <summary>Encola una sincronización, por ejemplo al conectar o al activar un calendario (design.md Decisión 4).</summary>
public static class CalendarSyncJobs
{
    public static void Enqueue(IBackgroundJobQueue queue, Guid userId) =>
        queue.Enqueue((services, cancellationToken) =>
            services.GetRequiredService<ICalendarSynchronizer>().SyncAsync(userId, cancellationToken));
}
