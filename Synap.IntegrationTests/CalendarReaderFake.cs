using Synap.Application.Features.Calendar;
using Synap.Domain;

namespace Synap.IntegrationTests;

/// <summary>
/// Hace de calendario conectado en los tests que no pasan por un proveedor. Por defecto no hay
/// calendario, que es lo que esperan los tests del briefing escritos antes de calendar-integration.
/// </summary>
public sealed class CalendarReaderFake : ICalendarReader
{
    public CalendarReadResult Result { get; set; } = new CalendarReadResult.NotConnected();

    public List<Guid> ReadFor { get; } = [];

    /// <summary>Lo deja sin calendario para el siguiente test.</summary>
    public void Reset()
    {
        Result = new CalendarReadResult.NotConnected();
        ReadFor.Clear();
    }

    public void Returns(params CalendarEvent[] events) => Result = new CalendarReadResult.Ok(events);

    public void Unavailable() => Result = new CalendarReadResult.Unavailable("en un test");

    public void NeedsReconnect() => Result = new CalendarReadResult.NeedsReconnect();

    public Task<CalendarReadResult> ListEventsAsync(
        Guid userId, DateTime fromUtc, DateTime toUtc, int maxEvents, CancellationToken cancellationToken = default)
    {
        ReadFor.Add(userId);
        return Task.FromResult(Result);
    }
}
