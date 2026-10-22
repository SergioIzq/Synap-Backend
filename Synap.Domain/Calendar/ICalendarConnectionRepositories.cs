using SergioIzq.Domain.Kernel.Interfaces.Repositories;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Domain;

/// <summary>
/// La conexión de un usuario se busca por su id (<see cref="IWriteRepository{TEntity,TId}.GetByIdAsync"/>
/// con el id del usuario), y se devuelve con seguimiento para poder marcarla o cambiar sus credenciales.
/// </summary>
public interface ICalendarConnectionWriteRepository : IWriteRepository<CalendarConnection, UserId>
{
}

public interface ICalendarConnectionReadRepository
{
    /// <summary>
    /// El estado de la conexión del usuario, sin las credenciales: la proyección no las lee, así
    /// que nada que responda "¿hay calendario?" puede revelarlas. Null si no hay conexión.
    /// </summary>
    Task<CalendarConnectionSummary?> GetSummaryAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record CalendarConnectionSummary(
    CalendarProvider Provider,
    string AccountLabel,
    DateTime ConnectedAt,
    DateTime? NeedsReconnectSince);
