using SergioIzq.Domain.Kernel.Interfaces.Repositories;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Domain;

/// <summary>
/// La conexión se busca por el id del usuario, que es su clave: `GetByIdAsync(userId)` devuelve la
/// conexión rastreada de ese usuario, o null si no tiene (design.md Decisión 2).
/// </summary>
public interface ICalendarConnectionRepository : IWriteRepository<CalendarConnection, UserId>
{
}
