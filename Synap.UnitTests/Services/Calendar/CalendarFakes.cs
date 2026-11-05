using Synap.Domain;
using Synap.Infrastructure.Services.Calendar;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.UnitTests.Services.Calendar;

internal sealed class FakeCalendarConnectionRepository : ICalendarConnectionWriteRepository
{
    public Dictionary<Guid, CalendarConnection> Connections { get; } = [];

    public CalendarConnection Add(CalendarConnection connection)
    {
        Connections[connection.UserId.Value] = connection;
        return connection;
    }

    public Task<CalendarConnection?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Connections.GetValueOrDefault(id));

    void SergioIzq.Domain.Kernel.Interfaces.Repositories.IWriteRepository<CalendarConnection, UserId>.Add(
        CalendarConnection entity) => Add(entity);

    public Task CreateAsync(CalendarConnection entity, CancellationToken cancellationToken)
    {
        Add(entity);
        return Task.CompletedTask;
    }

    public void Update(CalendarConnection entity)
    {
    }

    public void Delete(CalendarConnection entity) => Connections.Remove(entity.UserId.Value);
}
