using Synap.Domain;

namespace Synap.Application.Features.Calendar;

/// <summary>
/// Los calendarios copiados de un usuario, para la API y Configuración (calendar-store-and-view
/// design.md Decisión 6). Todo se filtra por el usuario: un calendario de otro no existe. Lo que
/// cambia se guarda con el <c>IUnitOfWork</c>; los borrados de eventos se ejecutan al momento.
/// </summary>
public interface ICalendarStore
{
    /// <summary>Los calendarios del usuario, por nombre, sin seguimiento.</summary>
    Task<IReadOnlyList<Domain.Calendar>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>El calendario con seguimiento para cambiarlo; null si no existe o es de otro usuario.</summary>
    Task<Domain.Calendar?> GetForUpdateAsync(Guid userId, Guid calendarId, CancellationToken cancellationToken = default);

    /// <summary>Borra la copia de eventos de un calendario, por ejemplo al desactivarlo.</summary>
    Task DeleteEventsAsync(Guid userId, Guid calendarId, CancellationToken cancellationToken = default);

    /// <summary>Borra los calendarios del usuario con sus eventos: la conexión cambió de cuenta.</summary>
    Task DeleteAllAsync(Guid userId, CancellationToken cancellationToken = default);
}
