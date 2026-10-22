using Synap.Application.Features.Calendar;
using Synap.Domain;

namespace Synap.UnitTests.Features.Briefing;

/// <summary>
/// Hace de calendario conectado del usuario. Por defecto no hay ninguno, que es lo que esperan los
/// tests del briefing escritos antes de calendar-integration.
/// </summary>
internal sealed class FakeCalendarReader : ICalendarReader
{
    public CalendarReadResult Result { get; set; } = new CalendarReadResult.NotConnected();
    private bool _hang;

    public List<(Guid UserId, DateTime FromUtc, DateTime ToUtc)> Reads { get; } = [];

    /// <summary>Los eventos del intervalo, en el orden en que el calendario los diera.</summary>
    public void Returns(params CalendarEvent[] events) => Result = new CalendarReadResult.Ok(events);

    public void Unavailable() => Result = new CalendarReadResult.Unavailable("en un test");

    public void NeedsReconnect() => Result = new CalendarReadResult.NeedsReconnect();

    /// <summary>Makes ListEventsAsync block until the cancellation token fires (simulates a timeout).</summary>
    public void HangForever() => _hang = true;

    public async Task<CalendarReadResult> ListEventsAsync(
        Guid userId, DateTime fromUtc, DateTime toUtc, int maxEvents, CancellationToken cancellationToken = default)
    {
        Reads.Add((userId, fromUtc, toUtc));
        if (_hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        return Result;
    }
}
