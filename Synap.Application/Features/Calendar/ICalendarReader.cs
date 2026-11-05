using Synap.Domain;

namespace Synap.Application.Features.Calendar;

/// <summary>
/// Lee los eventos de la copia de los calendarios activos de un usuario (calendar-store-and-view
/// design.md Decisión 5). Es el único puerto que conocen el asistente, el briefing y la vista, y
/// nunca espera al servidor: sincronizar es cosa de <see cref="ICalendarSynchronizer"/>.
/// </summary>
public interface ICalendarReader
{
    /// <summary>
    /// Las ocurrencias de [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), ordenadas por
    /// inicio y como mucho <paramref name="maxEvents"/>, limitadas a <paramref name="scope"/> si se
    /// da. Las repeticiones se expanden en la zona horaria del usuario.
    /// </summary>
    Task<CalendarReadResult> ListEventsAsync(
        Guid userId,
        DateTime fromUtc,
        DateTime toUtc,
        int maxEvents,
        CalendarScope? scope = null,
        CancellationToken cancellationToken = default);
}
