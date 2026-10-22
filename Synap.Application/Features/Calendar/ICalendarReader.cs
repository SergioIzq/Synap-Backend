using Synap.Domain;

namespace Synap.Application.Features.Calendar;

/// <summary>
/// Lee el calendario conectado de un usuario (calendar-integration design.md Decisión 1). Es el
/// único puerto que conocen el asistente y el briefing: no saben si los eventos vienen de Google o
/// de CalDAV, y no hay camino de escritura.
/// </summary>
public interface ICalendarReader
{
    /// <summary>
    /// Los eventos de [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), ordenados por inicio y
    /// como mucho <paramref name="maxEvents"/>. Nunca lanza por un calendario que falla: todo
    /// fallo vuelve como <see cref="CalendarReadResult.NeedsReconnect"/> o
    /// <see cref="CalendarReadResult.Unavailable"/>.
    /// </summary>
    Task<CalendarReadResult> ListEventsAsync(
        Guid userId, DateTime fromUtc, DateTime toUtc, int maxEvents, CancellationToken cancellationToken = default);
}
