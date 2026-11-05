using Microsoft.EntityFrameworkCore;
using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Data.Calendar;

public sealed class CalendarConnectionWriteRepository
    : AbsWriteRepository<CalendarConnection, UserId>, ICalendarConnectionWriteRepository
{
    public CalendarConnectionWriteRepository(SynapDbContext context) : base(context)
    {
    }
}

public sealed class CalendarConnectionReadRepository : ICalendarConnectionReadRepository
{
    private readonly SynapDbContext _context;

    public CalendarConnectionReadRepository(SynapDbContext context)
    {
        _context = context;
    }

    public async Task<CalendarConnectionSummary?> GetSummaryAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var id = UserId.CreateFromDatabase(userId);
        var summary = await _context.Set<CalendarConnection>()
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new CalendarConnectionSummary(c.Provider, c.AccountLabel, c.ConnectedAt, c.NeedsReconnectSince, c.CalendarRef, null))
            .FirstOrDefaultAsync(cancellationToken);

        if (summary is null)
        {
            return null;
        }

        // La del calendario activo más atrasado: es desde cuándo puede faltar algo en la copia.
        var lastSyncedAt = await _context.Set<Domain.Calendar>()
            .AsNoTracking()
            .Where(c => c.UserId == id && c.Enabled && c.LastSyncedAt != null)
            .MinAsync(c => c.LastSyncedAt, cancellationToken);

        return summary with { LastSyncedAt = lastSyncedAt };
    }
}
