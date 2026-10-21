using Synap.Domain;

namespace Synap.Application.Features.Calendar;

/// <summary>
/// El único punto por el que el asistente y el briefing leen el calendario de un usuario, sin
/// saber de proveedores (calendar-integration design.md Decisión 1). Nunca lanza por un fallo del
/// proveedor: lo devuelve como <see cref="CalendarReadResult"/>.
/// </summary>
public interface ICalendarReader
{
    /// <summary>
    /// Los eventos del calendario principal que se solapan con [<paramref name="fromUtc"/>,
    /// <paramref name="toUtc"/>), ordenados por inicio y como mucho <paramref name="maxEvents"/>.
    /// </summary>
    Task<CalendarReadResult> ListEventsAsync(
        Guid userId, DateTime fromUtc, DateTime toUtc, int maxEvents, CancellationToken cancellationToken = default);
}

/// <summary>Los límites de una lectura (specs/calendar-integration "Qué se lee de un evento").</summary>
public static class CalendarLimits
{
    public const int MaxEventsPerRead = 50;
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(92);
}
