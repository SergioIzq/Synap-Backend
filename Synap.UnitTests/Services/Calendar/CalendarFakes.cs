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

/// <summary>Un adaptador que responde lo que el test le diga y recuerda lo que se le pidió.</summary>
internal sealed class FakeProviderClient(CalendarProvider provider) : ICalendarProviderClient
{
    public CalendarProvider Provider { get; } = provider;

    public Func<CalendarProviderRequest, CalendarProviderRead> Responder { get; set; } =
        _ => new CalendarProviderRead(new CalendarReadResult.Ok([]));

    public List<CalendarProviderRequest> Requests { get; } = [];

    public Task<CalendarProviderRead> ReadAsync(CalendarProviderRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Responder(request));
    }
}
