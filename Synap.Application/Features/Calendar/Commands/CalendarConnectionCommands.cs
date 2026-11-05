using Microsoft.Extensions.Logging;
using SergioIzq.Application.Kernel.Interfaces;
using SergioIzq.Application.Kernel.Messaging;
using SergioIzq.Domain.Kernel.Abstractions.Results;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application;
using Synap.Shared.Application.BackgroundJobs;

namespace Synap.Application.Features.Calendar.Commands;

/// <summary>
/// Conecta un calendario CalDAV (specs/calendar-integration "Conectar un calendario CalDAV"): la
/// URL tiene que ser HTTPS y, antes de guardar nada, se comprueban las credenciales y se localiza
/// el home de la cuenta. Si algo falla, la conexión anterior queda como estaba.
///
/// Conectar otra cuenta u otro home borra los calendarios copiados de la anterior; volver a
/// conectar la misma (una reconexión) los conserva con sus preferencias. En ambos casos se encola
/// la primera sincronización (calendar-store-and-view design.md Decisión 4).
/// </summary>
public sealed record ConnectCalDavCalendarCommand(string? Url, string? Username, string? Password)
    : ICommand<CalendarSettingsResponse>;

public sealed class ConnectCalDavCalendarCommandHandler
    : ICommandHandler<ConnectCalDavCalendarCommand, CalendarSettingsResponse>
{
    private readonly ICalendarConnectionWriteRepository _connections;
    private readonly ICalendarConnectionReadRepository _connectionView;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;
    private readonly ICalDavDiscovery _discovery;
    private readonly CalendarCredentialsProtector _protector;
    private readonly ICalendarStore _calendars;
    private readonly IBackgroundJobQueue _jobs;
    private readonly ILogger<ConnectCalDavCalendarCommandHandler> _logger;

    public ConnectCalDavCalendarCommandHandler(
        ICalendarConnectionWriteRepository connections,
        ICalendarConnectionReadRepository connectionView,
        IUnitOfWork unitOfWork,
        IUserContext userContext,
        ICalDavDiscovery discovery,
        CalendarCredentialsProtector protector,
        ICalendarStore calendars,
        IBackgroundJobQueue jobs,
        ILogger<ConnectCalDavCalendarCommandHandler> logger)
    {
        _connections = connections;
        _connectionView = connectionView;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
        _discovery = discovery;
        _protector = protector;
        _calendars = calendars;
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<Result<CalendarSettingsResponse>> Handle(
        ConnectCalDavCalendarCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();
        var username = request.Username?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;

        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host))
        {
            return Result.Failure<CalendarSettingsResponse>(CalendarErrors.ServerUrlInvalid);
        }

        // Rechazada sin contactar con el servidor (specs "URL sin HTTPS").
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return Result.Failure<CalendarSettingsResponse>(CalendarErrors.HttpsRequired);
        }

        if (username.Length == 0 || password.Length == 0)
        {
            return Result.Failure<CalendarSettingsResponse>(CalendarErrors.CredentialsRequired);
        }

        var discovered = await _discovery.DiscoverAsync(url, username, password, cancellationToken);
        switch (discovered)
        {
            case CalDavDiscoveryResult.CredentialsRejected:
                return Result.Failure<CalendarSettingsResponse>(CalendarErrors.CredentialsRejected);
            case CalDavDiscoveryResult.Unreachable unreachable:
                _logger.LogWarning("No se pudo conectar el CalDAV de {UserId}: {Cause}", userId, unreachable.Cause);
                return Result.Failure<CalendarSettingsResponse>(CalendarErrors.Unreachable);
            case CalDavDiscoveryResult.NoCalendar:
                return Result.Failure<CalendarSettingsResponse>(CalendarErrors.NoCalendar);
        }

        var homeUrl = ((CalDavDiscoveryResult.Found)discovered).HomeUrl;

        // Otra cuenta u otro home: la copia anterior no es de esta conexión (specs "Reemplazo de la conexión").
        var previous = await _connectionView.GetSummaryAsync(userId, cancellationToken);
        if (previous is not null && (previous.AccountLabel != username || previous.CalendarRef != homeUrl))
        {
            await _calendars.DeleteAllAsync(userId, cancellationToken);
        }

        await CalendarConnections.ReplaceAsync(
            _connections,
            userId,
            CalendarProvider.CalDav,
            username,
            homeUrl,
            _protector.Protect(new CalendarCredentials.CalDav(username, password)),
            DateTime.UtcNow,
            cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        CalendarSyncJobs.Enqueue(_jobs, userId);

        return Result.Success(CalendarSettingsResponse.From(
            await _connectionView.GetSummaryAsync(userId, cancellationToken)));
    }
}

/// <summary>
/// Desconecta el calendario (specs/calendar-integration "Desconectar el calendario CalDAV"): borra
/// la conexión. Los recordatorios no se tocan. Desconectar sin conexión no es un error.
/// </summary>
public sealed record DisconnectCalendarCommand : ICommand<CalendarSettingsResponse>;

public sealed class DisconnectCalendarCommandHandler
    : ICommandHandler<DisconnectCalendarCommand, CalendarSettingsResponse>
{
    private readonly ICalendarConnectionWriteRepository _connections;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _userContext;

    public DisconnectCalendarCommandHandler(
        ICalendarConnectionWriteRepository connections,
        IUnitOfWork unitOfWork,
        IUserContext userContext)
    {
        _connections = connections;
        _unitOfWork = unitOfWork;
        _userContext = userContext;
    }

    public async Task<Result<CalendarSettingsResponse>> Handle(
        DisconnectCalendarCommand request, CancellationToken cancellationToken)
    {
        var userId = _userContext.RequireUserId();
        var connection = await _connections.GetByIdAsync(userId, cancellationToken);

        if (connection is not null)
        {
            _connections.Delete(connection);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(CalendarSettingsResponse.From(null));
    }
}
