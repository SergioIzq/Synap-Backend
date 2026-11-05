using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;
using StoredCalendar = Synap.Domain.Calendar;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// <see cref="ICalendarReader"/> sobre la copia (calendar-store-and-view design.md Decisión 5):
/// 1. elige en SQL los candidatos del intervalo en los calendarios activos del usuario: los que no
///    se repiten y se solapan, y los recurrentes que empezaron antes del final y no terminaron
///    antes del principio;
/// 2. expande cada uno con Ical.Net en la zona del usuario (<see cref="IcsOccurrences"/>).
///
/// Nunca contacta con el servidor. Los registros llevan solo ids y recuentos.
/// </summary>
public sealed class CalendarReader : ICalendarReader
{
    /// <summary>Recursos que se leen como mucho en una consulta, antes de expandir.</summary>
    public const int MaxCandidates = 2000;

    private readonly SynapDbContext _context;
    private readonly ILogger<CalendarReader> _logger;

    public CalendarReader(SynapDbContext context, ILogger<CalendarReader> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<CalendarReadResult> ListEventsAsync(
        Guid userId,
        DateTime fromUtc,
        DateTime toUtc,
        int maxEvents,
        CalendarScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var user = UserId.CreateFromDatabase(userId);

        var connection = await _context.Set<CalendarConnection>()
            .AsNoTracking()
            .Where(c => c.Id == user)
            .Select(c => new { c.AccountLabel, c.NeedsReconnectSince })
            .FirstOrDefaultAsync(cancellationToken);

        if (connection is null)
        {
            return new CalendarReadResult.NotConnected();
        }

        var timezone = await _context.Set<User>()
            .AsNoTracking()
            .Where(u => u.Id == user)
            .Select(u => u.Timezone)
            .FirstOrDefaultAsync(cancellationToken);

        var calendars = await _context.Set<StoredCalendar>()
            .AsNoTracking()
            .Where(c => c.UserId == user && c.Enabled)
            .ToListAsync(cancellationToken);

        var state = new CalendarCopyState(
            calendars.Where(c => c.LastSyncedAt is not null).Min(c => c.LastSyncedAt),
            connection.NeedsReconnectSince is not null,
            calendars.Any(c => c.LastSyncError is not null));

        var shown = calendars.Where(c => scope is null || c.Scope == scope).ToDictionary(c => c.Id);
        if (shown.Count == 0 || maxEvents <= 0 || toUtc <= fromUtc)
        {
            return new CalendarReadResult.Ok([], state);
        }

        // Un día de margen: los eventos de todo el día y las horas flotantes se guardan sin zona.
        var from = DateTime.SpecifyKind(fromUtc.AddDays(-1), DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(toUtc.AddDays(1), DateTimeKind.Utc);
        var ids = shown.Keys.ToList();

        var candidates = await _context.Set<CalendarEventResource>()
            .AsNoTracking()
            .Where(e => e.UserId == user && ids.Contains(e.CalendarId) && e.StartsAtUtc < to)
            .Where(e => e.EndsAtUtc == null ? e.IsRecurring || e.StartsAtUtc >= from : e.EndsAtUtc >= from)
            .OrderBy(e => e.StartsAtUtc)
            .Take(MaxCandidates)
            .Select(e => new { e.CalendarId, e.Ics })
            .ToListAsync(cancellationToken);

        var events = new List<CalendarEvent>();
        var unreadable = 0;
        foreach (var candidate in candidates)
        {
            var calendar = shown[candidate.CalendarId];
            var source = new CalendarEventSource(calendar.Id.Value, calendar.DisplayName, calendar.EffectiveColor, calendar.Scope);

            try
            {
                events.AddRange(IcsOccurrences
                    .Expand(candidate.Ics, connection.AccountLabel, timezone, fromUtc, toUtc)
                    .Select(e => e with { Calendar = source }));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Un recurso ilegible no cuesta el resto de eventos.
                unreadable++;
            }
        }

        if (unreadable > 0)
        {
            _logger.LogWarning("{Count} eventos de la copia de {UserId} no se pudieron leer", unreadable, userId);
        }

        return new CalendarReadResult.Ok(
            [.. events.OrderBy(e => e.StartUtc).ThenBy(e => e.EndUtc).Take(maxEvents)], state);
    }
}
