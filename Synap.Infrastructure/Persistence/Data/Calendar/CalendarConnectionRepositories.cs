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

    public Task<CalendarConnectionSummary?> GetSummaryAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var id = UserId.CreateFromDatabase(userId);
        return _context.Set<CalendarConnection>()
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new CalendarConnectionSummary(c.Provider, c.AccountLabel, c.ConnectedAt, c.NeedsReconnectSince))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
