using Synap.Application.Features.Calendar;
using Synap.Domain;
using System.Collections.Concurrent;

namespace Synap.IntegrationTests;

/// <summary>
/// Hace de copia del calendario en los tests que no pasan por un servidor. Por defecto no hay
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

    public void Returns(params CalendarEvent[] events) =>
        Result = new CalendarReadResult.Ok(events, CalendarCopyState.Fresh(DateTime.UtcNow));

    /// <summary>Una copia que no se pudo poner al día.</summary>
    public void Stale(DateTime? lastSyncedAt, bool needsReconnect = false, params CalendarEvent[] events) =>
        Result = new CalendarReadResult.Ok(events, new CalendarCopyState(lastSyncedAt, needsReconnect, SyncFailing: !needsReconnect));

    public Task<CalendarReadResult> ListEventsAsync(
        Guid userId, DateTime fromUtc, DateTime toUtc, int maxEvents, CalendarScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        ReadFor.Add(userId);
        return Task.FromResult(Result);
    }
}

/// <summary>
/// Hace de sincronización en el host de pruebas: nada sale a la red. Responde
/// <see cref="Outcome"/> y recuerda para quién se pidió.
/// </summary>
public sealed class CalendarSynchronizerFake : ICalendarSynchronizer
{
    public CalendarSyncOutcome Outcome { get; set; } = CalendarSyncOutcome.Synced;

    public ConcurrentBag<Guid> SyncedFor { get; } = [];

    public Task<CalendarSyncOutcome> SyncAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        SyncedFor.Add(userId);
        return Task.FromResult(Outcome);
    }
}
