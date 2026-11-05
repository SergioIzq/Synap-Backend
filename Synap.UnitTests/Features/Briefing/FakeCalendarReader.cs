using Synap.Application.Features.Calendar;
using Synap.Domain;

namespace Synap.UnitTests.Features.Briefing;

/// <summary>
/// Hace de copia del calendario del usuario. Por defecto no hay ninguno, que es lo que esperan los
/// tests del briefing escritos antes de calendar-integration.
/// </summary>
internal sealed class FakeCalendarReader : ICalendarReader
{
    public CalendarReadResult Result { get; set; } = new CalendarReadResult.NotConnected();

    public List<(Guid UserId, DateTime FromUtc, DateTime ToUtc)> Reads { get; } = [];

    public List<CalendarScope?> Scopes { get; } = [];

    /// <summary>Los eventos del intervalo, en el orden en que la copia los diera, con la copia al día.</summary>
    public void Returns(params CalendarEvent[] events) =>
        Result = new CalendarReadResult.Ok(events, CalendarCopyState.Fresh(DateTime.UtcNow));

    /// <summary>Una copia que puede no estar al día: la última sincronización correcta y si hay que reconectar.</summary>
    public void Stale(DateTime? lastSyncedAt, bool needsReconnect = false, params CalendarEvent[] events) =>
        Result = new CalendarReadResult.Ok(events, new CalendarCopyState(lastSyncedAt, needsReconnect, SyncFailing: !needsReconnect));

    public Task<CalendarReadResult> ListEventsAsync(
        Guid userId, DateTime fromUtc, DateTime toUtc, int maxEvents, CalendarScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        Reads.Add((userId, fromUtc, toUtc));
        Scopes.Add(scope);
        if (Result is CalendarReadResult.Ok ok && scope is { } wanted)
        {
            return Task.FromResult<CalendarReadResult>(ok with { Events = [.. ok.Events.Where(e => e.Scope == wanted)] });
        }

        return Task.FromResult(Result);
    }
}

/// <summary>Hace de sincronización previa al briefing: responde lo que el test diga, o se cuelga.</summary>
internal sealed class FakeCalendarSynchronizer : ICalendarSynchronizer
{
    private bool _hang;

    public CalendarSyncOutcome Outcome { get; set; } = CalendarSyncOutcome.Synced;

    public List<Guid> Synced { get; } = [];

    /// <summary>No termina hasta que se cancela: un servidor que no responde.</summary>
    public void HangForever() => _hang = true;

    public async Task<CalendarSyncOutcome> SyncAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        Synced.Add(userId);
        if (_hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return Outcome;
    }
}
