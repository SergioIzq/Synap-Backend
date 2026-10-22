using Synap.Application.Features.Calendar;
using Synap.Domain;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Un adaptador de lectura por proveedor (calendar-integration design.md Decisión 1). Solo lo usa
/// <see cref="CalendarReader"/>, que es quien carga, descifra y guarda.
/// </summary>
public interface ICalendarProviderClient
{
    CalendarProvider Provider { get; }

    /// <summary>
    /// Lee el intervalo. Nunca lanza por un fallo del proveedor: lo devuelve como
    /// <see cref="CalendarReadResult.NeedsReconnect"/> o <see cref="CalendarReadResult.Unavailable"/>.
    /// </summary>
    Task<CalendarProviderRead> ReadAsync(CalendarProviderRequest request, CancellationToken cancellationToken);
}

public sealed record CalendarProviderRequest(
    Guid UserId,
    string CalendarRef,
    CalendarCredentials Credentials,
    DateTime FromUtc,
    DateTime ToUtc,
    int MaxEvents);

/// <summary>
/// El resultado y, si el adaptador renovó algo (el access token de Google), las credenciales
/// nuevas para que el despachador las guarde cifradas.
/// </summary>
public sealed record CalendarProviderRead(CalendarReadResult Result, CalendarCredentials? RenewedCredentials = null);
