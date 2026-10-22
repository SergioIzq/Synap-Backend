using Microsoft.Extensions.Logging;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Application.Features.Calendar;
using Synap.Domain;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// El despachador de <see cref="ICalendarReader"/> (calendar-integration design.md Decisión 1):
/// carga la conexión del usuario, descifra sus credenciales y delega en el adaptador de su
/// proveedor. También es quien guarda lo que la lectura cambia: el access token renovado y la marca
/// de reconexión.
///
/// Los registros llevan solo usuario, proveedor, estado y causa (design.md Decisión 10).
/// </summary>
public sealed class CalendarReader : ICalendarReader
{
    private readonly ICalendarConnectionWriteRepository _connections;
    private readonly IUnitOfWork _unitOfWork;
    private readonly CalendarCredentialsProtector _credentialsProtector;
    private readonly IEnumerable<ICalendarProviderClient> _clients;
    private readonly ILogger<CalendarReader> _logger;

    public CalendarReader(
        ICalendarConnectionWriteRepository connections,
        IUnitOfWork unitOfWork,
        CalendarCredentialsProtector credentialsProtector,
        IEnumerable<ICalendarProviderClient> clients,
        ILogger<CalendarReader> logger)
    {
        _connections = connections;
        _unitOfWork = unitOfWork;
        _credentialsProtector = credentialsProtector;
        _clients = clients;
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

        // Una conexión marcada no se vuelve a intentar: solo la quita reconectar
        // (specs/calendar-integration "Reconexión").
        if (connection.NeedsReconnect)
        {
            return new CalendarReadResult.NeedsReconnect();
        }

        var credentials = _credentialsProtector.TryRead(connection.Provider, connection.CredentialsEncrypted);
        if (credentials is null)
        {
            // Normalmente, la clave maestra cambió. Igual que con la key de Groq: hay que reconectar.
            return await MarkNeedsReconnectAsync(connection, "credenciales ilegibles", cancellationToken);
        }

        var client = _clients.FirstOrDefault(c => c.Provider == connection.Provider);
        if (client is null)
        {
            return Unavailable(connection, "proveedor sin adaptador");
        }

        CalendarProviderRead read;
        try
        {
            read = await client.ReadAsync(
                new CalendarProviderRequest(userId, connection.CalendarRef, credentials, fromUtc, toUtc, maxEvents),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Un adaptador no debería lanzar; si lo hace, se registra el tipo y nunca el mensaje,
            // que podría llevar contenido de la respuesta.
            return Unavailable(connection, exception.GetType().Name);
        }

        if (read.RenewedCredentials is { } renewed)
        {
            connection.ReplaceCredentials(_credentialsProtector.Protect(renewed));
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return read.Result switch
        {
            CalendarReadResult.NeedsReconnect => await MarkNeedsReconnectAsync(connection, "acceso rechazado", cancellationToken),
            CalendarReadResult.Unavailable unavailable => Unavailable(connection, unavailable.Cause),
            CalendarReadResult.Ok ok => new CalendarReadResult.Ok(
                [.. ok.Events.OrderBy(e => e.StartUtc).ThenBy(e => e.EndUtc).Take(Math.Max(0, maxEvents))]),
            var other => other,
        };
    }

    private async Task<CalendarReadResult> MarkNeedsReconnectAsync(
        CalendarConnection connection, string cause, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Calendario de {UserId} ({Provider}) marcado para reconectar: {Cause}",
            connection.UserId.Value, connection.Provider, cause);

        connection.MarkNeedsReconnect(DateTime.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new CalendarReadResult.NeedsReconnect();
    }

    private CalendarReadResult.Unavailable Unavailable(CalendarConnection connection, string cause)
    {
        _logger.LogWarning(
            "Calendario de {UserId} ({Provider}) no disponible: {Cause}",
            connection.UserId.Value, connection.Provider, cause);

        return new CalendarReadResult.Unavailable(cause);
    }
}
