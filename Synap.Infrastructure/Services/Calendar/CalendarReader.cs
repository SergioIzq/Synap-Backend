using Microsoft.Extensions.Logging;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Calendar;
using Synap.Domain;
using Synap.Shared.Application.Interfaces;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Carga la conexión del usuario, descifra sus credenciales y delega en el adaptador de su
/// proveedor (calendar-integration design.md Decisiones 1 y 2). Es lo único que marca una conexión
/// para reconectar o guarda un token renovado.
///
/// Los registros llevan el usuario, el proveedor y la causa, nunca nada de un evento ni de una
/// credencial (design.md Decisión 10).
/// </summary>
public sealed class CalendarReader : ICalendarReader
{
    private readonly ICalendarConnectionRepository _connections;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISecretProtector _secretProtector;
    private readonly IEnumerable<ICalendarProviderClient> _providers;
    private readonly ILogger<CalendarReader> _logger;

    public CalendarReader(
        ICalendarConnectionRepository connections,
        IUnitOfWork unitOfWork,
        ISecretProtector secretProtector,
        IEnumerable<ICalendarProviderClient> providers,
        ILogger<CalendarReader> logger)
    {
        _connections = connections;
        _unitOfWork = unitOfWork;
        _secretProtector = secretProtector;
        _providers = providers;
        _logger = logger;
    }

    public async Task<CalendarReadResult> ListEventsAsync(
        Guid userId, DateTime fromUtc, DateTime toUtc, int maxEvents, CancellationToken cancellationToken = default)
    {
        var connection = await _connections.GetByIdAsync(userId, cancellationToken);
        if (connection is null)
        {
            return new CalendarReadResult.NotConnected();
        }

        // Una conexión marcada no se vuelve a intentar: solo una reconexión quita la marca.
        if (connection.NeedsReconnect)
        {
            return new CalendarReadResult.NeedsReconnect();
        }

        if (!_secretProtector.TryUnprotect(connection.CredentialsEncrypted, out var credentials))
        {
            return await MarkNeedsReconnectAsync(connection, "las credenciales no se pueden descifrar", cancellationToken);
        }

        var provider = _providers.FirstOrDefault(p => p.Provider == connection.Provider);
        if (provider is null)
        {
            _logger.LogError("No hay adaptador para el proveedor de calendario {Provider}", connection.Provider);
            return new CalendarReadResult.Unavailable("proveedor no disponible");
        }

        if (toUtc - fromUtc > CalendarLimits.MaxRange)
        {
            toUtc = fromUtc + CalendarLimits.MaxRange;
        }

        var max = Math.Clamp(maxEvents, 1, CalendarLimits.MaxEventsPerRead);
        var result = await provider.ListEventsAsync(connection.CalendarRef, credentials, fromUtc, toUtc, max, cancellationToken);

        switch (result)
        {
            case ProviderReadResult.Ok ok:
                if (ok.RenewedCredentials is not null)
                {
                    connection.UpdateCredentials(_secretProtector.Protect(ok.RenewedCredentials));
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                }

                return new CalendarReadResult.Ok(ok.Events.OrderBy(e => e.StartUtc).Take(max).ToList());

            case ProviderReadResult.Rejected rejected:
                return await MarkNeedsReconnectAsync(connection, rejected.Cause, cancellationToken);

            case ProviderReadResult.Unavailable unavailable:
                _logger.LogWarning(
                    "No se pudo leer el calendario {Provider} del usuario {UserId}: {Cause}",
                    connection.Provider, userId, unavailable.Cause);
                return new CalendarReadResult.Unavailable(unavailable.Cause);

            default:
                throw new InvalidOperationException($"Resultado de proveedor desconocido: {result.GetType().Name}");
        }
    }

    private async Task<CalendarReadResult> MarkNeedsReconnectAsync(
        CalendarConnection connection, string cause, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "El calendario {Provider} del usuario {UserId} necesita reconectarse: {Cause}",
            connection.Provider, connection.Id.Value, cause);

        connection.MarkNeedsReconnect(DateTime.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new CalendarReadResult.NeedsReconnect();
    }
}
