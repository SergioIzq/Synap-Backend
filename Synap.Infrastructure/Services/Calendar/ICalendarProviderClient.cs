using Synap.Domain;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Un adaptador de proveedor (Google o CalDAV). Recibe las credenciales ya descifradas, en el
/// formato JSON que él mismo define, y nunca toca la base de datos: de eso se encarga
/// <see cref="CalendarReader"/>.
/// </summary>
public interface ICalendarProviderClient
{
    CalendarProvider Provider { get; }

    Task<ProviderReadResult> ListEventsAsync(
        string calendarRef, string credentials, DateTime fromUtc, DateTime toUtc, int maxEvents, CancellationToken cancellationToken);
}

public abstract record ProviderReadResult
{
    private ProviderReadResult()
    {
    }

    /// <summary><paramref name="RenewedCredentials"/> viene cuando el adaptador renovó un token y hay que guardarlo.</summary>
    public sealed record Ok(IReadOnlyList<CalendarEvent> Events, string? RenewedCredentials = null) : ProviderReadResult;

    /// <summary>El proveedor rechazó las credenciales: acceso revocado, caducado o contraseña cambiada.</summary>
    public sealed record Rejected(string Cause) : ProviderReadResult;

    /// <summary>Fallo temporal: no se marca la conexión.</summary>
    public sealed record Unavailable(string Cause) : ProviderReadResult;
}
