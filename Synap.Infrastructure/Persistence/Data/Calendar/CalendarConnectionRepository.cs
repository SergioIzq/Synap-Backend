using Synap.Domain;
using Synap.Infrastructure.Persistence.Command;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Infrastructure.Persistence.Data.Calendar;

public sealed class CalendarConnectionRepository : AbsWriteRepository<CalendarConnection, UserId>, ICalendarConnectionRepository
{
    public CalendarConnectionRepository(SynapDbContext context) : base(context)
    {
    }
}
