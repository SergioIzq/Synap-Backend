using Microsoft.EntityFrameworkCore;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Data.Calendar;

public sealed class CalendarStore : ICalendarStore
{
    private readonly SynapDbContext _context;

    public CalendarStore(SynapDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Domain.Calendar>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var id = UserId.CreateFromDatabase(userId);
        return await _context.Set<Domain.Calendar>()
            .AsNoTracking()
            .Where(c => c.UserId == id)
            .OrderBy(c => c.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public Task<Domain.Calendar?> GetForUpdateAsync(Guid userId, Guid calendarId, CancellationToken cancellationToken = default)
    {
        var user = UserId.CreateFromDatabase(userId);
        var id = CalendarId.CreateFromDatabase(calendarId);
        return _context.Set<Domain.Calendar>()
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == user, cancellationToken);
    }

    public Task DeleteEventsAsync(Guid userId, Guid calendarId, CancellationToken cancellationToken = default)
    {
        var user = UserId.CreateFromDatabase(userId);
        var id = CalendarId.CreateFromDatabase(calendarId);
        return _context.Set<CalendarEventResource>()
            .Where(e => e.CalendarId == id && e.UserId == user)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task DeleteAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = UserId.CreateFromDatabase(userId);
        return _context.Set<Domain.Calendar>().Where(c => c.UserId == user).ExecuteDeleteAsync(cancellationToken);
    }
}
